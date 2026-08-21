using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace apps.Components.Windows;

/// <summary>
/// Discovers installed Windows applications from the "Programs and Features" uninstall registry keys
/// (per-machine 64-bit and 32-bit views plus the per-user hive) and, when <c>winget</c> is present,
/// enriches each entry with its winget package id so a later update check can resolve the latest version.
/// Packages that winget knows about but the registry does not (e.g. MSIX / Store apps) are emitted too.
/// </summary>
public sealed class WindowsApplicationsScanner(IProcessRunner runner, ILogger<WindowsApplicationsScanner> logger)
    : IScanner
{
    private const char TruncationMarker = '…';

    private string? _wingetExecutablePath;

    public string Name => "WindowsApplication";

    /// <inheritdoc/>
    public string DisplayName => "Application";

    /// <inheritdoc/>
    public string ProgressLabel => "Applications";

    /// <inheritdoc/>
    public string ProgressItemNoun => "app";

    public OS SupportedOS => OS.Windows;
    public AppKind Kind => AppKind.App;

    /// <inheritdoc/>
    public bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        _wingetExecutablePath = ScannerHelper.FindExecutable("winget");
        if (_wingetExecutablePath is null)
        {
            logger.LogDebug("winget not found; falling back to registry-only discovery");
        }

        // The uninstall registry keys are always present on Windows, so discovery is always possible.
        return true;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        var wingetPackages = await LoadWingetPackagesAsync(cancellationToken).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in EnumerateRegistryPrograms(wingetPackages, seen, cancellationToken))
        {
            yield return app;
        }

        foreach (var package in wingetPackages.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(package.Id) || seen.Contains(package.Name))
            {
                continue;
            }

            logger.LogDebug("Discovered winget-only package {Name} [{Id}] v{Version}", package.Name, package.Id, package.Version ?? "—");

            yield return new DiscoveredApp(this, package.Name,
                new AppIdentifier(Name, DisplayName, "winget"),
                AppKind.App)
            {
                PackageId = package.Id,
                InstalledVersion = package.Version,
                UpdateInfo = package.Id,
                Attribute = AppAttribute.App
            };
        }
    }

    /// <summary>
    /// Resolves the latest available version of each app through winget. Apps carrying a winget
    /// package id are checked concurrently with <c>winget show</c>; apps without one have no source to
    /// check against and stream through unchanged. When winget is unavailable, the checkable apps are
    /// flagged as errored since their update status cannot be determined.
    /// </summary>
    public async IAsyncEnumerable<(AppRecord App, bool Error)> CheckAsync(AppRecord[] apps, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var checkable = new List<AppRecord>();

        foreach (var record in apps)
        {
            if (string.IsNullOrEmpty(record.App.PackageId))
            {
                logger.LogDebug("No winget package id for {App}; skipping update check", record.App.Name);
                yield return (record, false);
            }
            else
            {
                checkable.Add(record);
            }
        }

        if (checkable.Count == 0)
        {
            yield break;
        }

        if (_wingetExecutablePath is null)
        {
            foreach (var record in checkable)
            {
                yield return (record, true);
            }

            yield break;
        }

        await foreach (var item in checkable.WhenAll<AppRecord, (AppRecord Record, bool Error)>(CheckPackageAsync, cancellationToken: cancellationToken))
        {
            yield return item;
        }
    }

    /// <summary>
    /// Resolves the latest version for a single app via <c>winget show</c> and writes the outcome to
    /// the shared channel. A failed subprocess is reported as an error; an unresolvable version is not.
    /// </summary>
    private async Task CheckPackageAsync(AppRecord record, ChannelWriter<(AppRecord Record, bool Error)> writer, CancellationToken cancellationToken)
    {
        try
        {
            var latest = await GetLatestVersionByWingetAsync(record.App.PackageId!, cancellationToken).ConfigureAwait(false);
            if (latest is not null)
            {
                record.App.LatestVersion = latest;
            }
            else
            {
                logger.LogDebug("winget reported no resolvable version for {App} [{Id}]", record.App.Name, record.App.PackageId);
            }

            await writer.WriteAsync((record, false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "winget update check failed for {App} [{Id}]", record.App.Name, record.App.PackageId);
            await writer.WriteAsync((record, true), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Queries <c>winget show --id &lt;id&gt; --exact</c> for the latest version published by the
    /// package's source. Returns <see langword="null"/> when winget exits non-zero or reports no
    /// concrete version (e.g. an "Unknown" version).
    /// </summary>
    private async Task<string?> GetLatestVersionByWingetAsync(string packageId, CancellationToken cancellationToken)
    {
        var args = $"show --id \"{packageId}\" --exact --disable-interactivity --accept-source-agreements";
        var result = await runner.RunAsync(_wingetExecutablePath!, args, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            logger.LogDebug("'winget show' for {Id} exited with {Code}: {Err}", packageId, result.ExitCode, result.StandardError.Trim());
            return null;
        }

        return ExtractWingetVersion(result.StandardOutput);
    }

    /// <summary>
    /// Reads the three uninstall registry locations, yielding one <see cref="DiscoveredApp"/> per
    /// genuine application. Updates, hotfixes, system components, and child component entries are
    /// filtered out, and duplicates across the 64-bit / 32-bit / per-user hives are collapsed by name.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private IEnumerable<DiscoveredApp> EnumerateRegistryPrograms(
        Dictionary<string, WingetPackage> wingetPackages,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        const string uninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        (RegistryHive Hive, RegistryView View)[] roots =
        [
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default)
        ];

        foreach (var (hive, view) in roots)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(uninstallSubKey);
            if (uninstallKey is null)
            {
                continue;
            }

            foreach (var app in EnumerateUninstallKey(uninstallKey, wingetPackages, seen, cancellationToken))
            {
                yield return app;
            }
        }
    }

    /// <summary>Yields every qualifying application directly under a single uninstall registry key.</summary>
    [SupportedOSPlatform("windows")]
    private IEnumerable<DiscoveredApp> EnumerateUninstallKey(
        RegistryKey uninstallKey,
        Dictionary<string, WingetPackage> wingetPackages,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        foreach (var subKeyName in uninstallKey.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var entry = uninstallKey.OpenSubKey(subKeyName);
            if (entry is null)
            {
                continue;
            }

            var app = ToDiscoveredApp(entry, wingetPackages, seen);
            if (app is not null)
            {
                yield return app;
            }
        }
    }

    /// <summary>
    /// Translates a single uninstall registry subkey into a <see cref="DiscoveredApp"/>, or returns
    /// <see langword="null"/> when the entry should be skipped (no name, an update/hotfix, a system
    /// component, a child component, or a duplicate already emitted).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private DiscoveredApp? ToDiscoveredApp(RegistryKey entry, Dictionary<string, WingetPackage> wingetPackages, HashSet<string> seen)
    {
        var name = Normalize(entry.GetValue("DisplayName") as string);
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (ReadInt(entry, "SystemComponent") == 1)
        {
            return null;
        }

        if (entry.GetValue("ReleaseType") is "Security Update" or "Update" or "Hotfix")
        {
            return null;
        }

        // Child entries (e.g. per-component patches) point at a parent and are not standalone apps.
        if (entry.GetValue("ParentKeyName") is string { Length: > 0 })
        {
            return null;
        }

        if (!seen.Add(name))
        {
            return null;
        }

        var version = Normalize(entry.GetValue("DisplayVersion") as string);
        var publisher = Normalize(entry.GetValue("Publisher") as string);
        var installLocation = Normalize(entry.GetValue("InstallLocation") as string);

        string? packageId = null;
        string? qualifier = null;
        if (wingetPackages.TryGetValue(name, out var package) && !string.IsNullOrEmpty(package.Id))
        {
            packageId = package.Id;
            qualifier = "winget";
            version ??= package.Version;
        }

        logger.LogDebug("Discovered application {Name} v{Version} [{Publisher}]", name, version ?? "—", publisher ?? "—");

        return new DiscoveredApp(this, name,
            new AppIdentifier(Name, DisplayName, qualifier),
            AppKind.App)
        {
            PackageId = packageId,
            InstalledVersion = version,
            Path = installLocation,
            Description = publisher,
            UpdateInfo = packageId,
            Attribute = AppAttribute.App
        };
    }

    /// <summary>
    /// Runs <c>winget list</c> and parses its table into a name-keyed map of installed packages.
    /// Returns an empty map when winget is unavailable or the output cannot be parsed — winget is an
    /// enrichment source, so any failure is non-fatal.
    /// </summary>
    private async Task<Dictionary<string, WingetPackage>> LoadWingetPackagesAsync(CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, WingetPackage>(StringComparer.OrdinalIgnoreCase);
        if (_wingetExecutablePath is null)
        {
            return map;
        }

        ProcessResult result;
        try
        {
            result = await runner.RunAsync(_wingetExecutablePath, "list --disable-interactivity --accept-source-agreements", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to run 'winget list'");
            return map;
        }

        if (!result.Success)
        {
            logger.LogWarning("'winget list' exited with {Code}: {Err}", result.ExitCode, result.StandardError.Trim());
            return map;
        }

        ParseWingetList(result.StandardOutput, map);
        logger.LogDebug("winget reported {Count} installed packages", map.Count);
        return map;
    }

    /// <summary>
    /// Runs the pure <see cref="ParseWingetList(string)"/> parser and merges its packages into
    /// <paramref name="map"/>, logging when the table header could not be located.
    /// </summary>
    private void ParseWingetList(string output, Dictionary<string, WingetPackage> map)
    {
        if (!ContainsWingetHeader(output))
        {
            logger.LogDebug("Could not locate the winget table header; skipping winget enrichment");
            return;
        }

        foreach (var package in ParseWingetList(output))
        {
            map[package.Name] = package;
        }
    }

    /// <summary>
    /// Parses the fixed-width table emitted by <c>winget list</c> using the header row to locate each
    /// column. Non-English headers and malformed rows are skipped rather than guessed at. When a name
    /// repeats, the last row seen wins. Returns an empty list when the header cannot be located.
    /// </summary>
    internal static List<WingetPackage> ParseWingetList(string output)
    {
        var packages = new List<WingetPackage>();
        var lines = output.Split('\n');

        var headerIndex = Array.FindIndex(lines, IsWingetHeader);
        if (headerIndex < 0)
        {
            return packages;
        }

        var header = lines[headerIndex];
        var idCol = header.IndexOf("Id", StringComparison.Ordinal);
        var versionCol = header.IndexOf("Version", StringComparison.Ordinal);
        var availableCol = header.IndexOf("Available", StringComparison.Ordinal);
        var sourceCol = header.IndexOf("Source", StringComparison.Ordinal);
        if (idCol < 0 || versionCol < 0)
        {
            return packages;
        }

        var versionEnd = availableCol > 0 ? availableCol : sourceCol > 0 ? sourceCol : int.MaxValue;

        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            if (TryParseWingetRow(lines[i], idCol, versionCol, versionEnd, out var package))
            {
                packages.Add(package);
            }
        }

        return packages;
    }

    /// <summary>Returns whether <paramref name="output"/> contains a recognisable winget table header.</summary>
    private static bool ContainsWingetHeader(string output) => Array.Exists(output.Split('\n'), IsWingetHeader);

    /// <summary>Returns whether <paramref name="line"/> is the <c>Name Id Version</c> table header row.</summary>
    private static bool IsWingetHeader(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("Name", StringComparison.Ordinal)
               && line.Contains("Id", StringComparison.Ordinal)
               && line.Contains("Version", StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses a single <c>winget list</c> data row by slicing it at the column offsets taken from the
    /// header. Returns <see langword="false"/> for the separator rule, blank lines, and rows missing a
    /// name or id.
    /// </summary>
    internal static bool TryParseWingetRow(string raw, int idCol, int versionCol, int versionEnd, out WingetPackage package)
    {
        package = default;

        var line = raw.TrimEnd();
        if (line.Length <= idCol || line.AsSpan().TrimStart().StartsWith("---"))
        {
            return false;
        }

        var name = Normalize(Slice(line, 0, idCol));
        var id = Normalize(Slice(line, idCol, versionCol));
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id))
        {
            return false;
        }

        // winget elides long names and ids with an ellipsis when the table is narrower than the
        // content. A truncated id is unusable for an exact lookup, so such rows are dropped rather
        // than risk persisting a package id that no later check could resolve.
        if (name.Contains(TruncationMarker) || id.Contains(TruncationMarker))
        {
            return false;
        }

        package = new WingetPackage(name, id, Normalize(Slice(line, versionCol, versionEnd)));
        return true;
    }

    /// <summary>
    /// Extracts the version from <c>winget show</c> key-value output by reading the first
    /// <c>Version:</c> line. Returns <see langword="null"/> when no concrete version is present.
    /// </summary>
    internal static string? ExtractWingetVersion(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var version = line["Version:".Length..].Trim();
            return string.IsNullOrEmpty(version) || version.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                ? null
                : version;
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static int? ReadInt(RegistryKey key, string name)
    {
        return key.GetValue(name) is int value ? value : null;
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string Slice(string line, int start, int end)
    {
        if (start >= line.Length)
        {
            return string.Empty;
        }

        var clampedEnd = Math.Min(end, line.Length);
        return line[start..clampedEnd].Trim();
    }

    /// <summary>An installed package as reported by <c>winget list</c>.</summary>
    internal readonly record struct WingetPackage(string Name, string Id, string? Version);
}
