using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

namespace apps.Components.Dotnet;

/// <summary>
/// Discovers .NET SDKs, runtimes, and global tools installed on the system.
/// Checks SDKs/runtimes against the .NET releases index and global tools against the NuGet registry.
/// </summary>
public sealed class DotnetScanner(IHttpClientFactory httpClientFactory, IProcessRunner runner, ILogger<DotnetScanner> logger)
    : IScanner
{
    /// <summary>
    /// A parsed .NET component line: an SDK channel, runtime, or global tool. For SDKs and runtimes
    /// <paramref name="Path"/> is the install directory; for global tools it is the invocation command.
    /// </summary>
    internal sealed record DotnetComponent(string Name, string Version, string Path);

    private readonly ConcurrentDictionary<string, Task<string?>> _inflightNuget = new(StringComparer.OrdinalIgnoreCase);

    private string? _executablePath;

    public string Name => "Dotnet";

    /// <inheritdoc/>
    public string DisplayName => ".NET";

    /// <inheritdoc/>
    public string ProgressLabel => ".NET";

    public OS SupportedOS => OS.MacOS | OS.Windows;
    public AppKind Kind => AppKind.DevTool;

    public bool IsAvailable()
    {
        _executablePath = ScannerHelper.FindExecutable("dotnet");
        return _executablePath is not null;
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var sdk in EnumerateSdks(cancellationToken))
        {
            yield return sdk;
        }

        await foreach (var runtime in EnumerateRuntimes(cancellationToken))
        {
            yield return runtime;
        }

        await foreach (var tool in EnumerateGlobalTools(cancellationToken))
        {
            yield return tool;
        }
    }

    public async IAsyncEnumerable<(AppRecord App, bool Error)> CheckAsync(AppRecord[] apps, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        DotnetReleasesIndex? releasesIndex = null;
        var releasesIndexFetchFailed = false;
        try
        {
            using var client = httpClientFactory.CreateClient("dotnet-releases");
            await using var stream = await client.GetStreamAsync("/dotnet/release-metadata/releases-index.json", cancellationToken).ConfigureAwait(false);
            releasesIndex = await JsonSerializer.DeserializeAsync(stream, DotnetJsonContext.Default.DotnetReleasesIndex, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch .NET releases index");
            releasesIndexFetchFailed = true;
        }

        var nugets = new List<AppRecord>();
        foreach (var record in apps)
        {
            if (record.App.Identifier.Qualifier == "Global Tool")
            {
                nugets.Add(record);
            }
            else
            {
                if (releasesIndex?.ReleasesIndex is null || record.App.InstalledVersion is null)
                {
                    // Fetch failure → the check could not run, count it as an error;
                    // a merely-missing installed version is not an error.
                    yield return (record, releasesIndexFetchFailed);
                    continue;
                }

                var channelVersion = MajorMinor(record.App.InstalledVersion);
                var channel = releasesIndex.ReleasesIndex.FirstOrDefault(c => c.ChannelVersion.Equals(channelVersion, StringComparison.OrdinalIgnoreCase));
                if (channel is null)
                {
                    logger.LogDebug("No .NET releases channel found for {Version}", channelVersion);
                    yield return (record, false);
                    continue;
                }

                record.App.LatestVersion = record.App.Identifier.Qualifier == "Runtime" ? channel.LatestRuntime : channel.LatestSdk;
                yield return (record, false);
            }
        }

        if (nugets.Count > 0)
        {
            await foreach (var item in nugets.WhenAll<AppRecord, (AppRecord Record, bool Error)>(CheckNuGetVersionAsync, cancellationToken: cancellationToken))
            {
                yield return item;
            }
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateRuntimes([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(_executablePath!, "--list-runtimes", cancellationToken);
        if (result.Success)
        {
            var lines = ParseRuntimes(result.StandardOutput);
            foreach (var line in lines)
            {
                yield return new DiscoveredApp(this, line.Name,
                    new AppIdentifier(Name, DisplayName, "Runtime"),
                    AppKind.DevTool)
                {
                    InstalledVersion = line.Version,
                    Path = Path.Combine(line.Path, line.Version),
                    Attribute = AppAttribute.DevTool | AppAttribute.Sdk,
                };
            }
        }
        else
        {
            logger.LogWarning("'dotnet --list-runtimes' failed: {Err}", result.StandardError.Trim());
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateSdks([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Output: "8.0.300 [/usr/local/share/dotnet/sdk]"
        var result = await runner.RunAsync(_executablePath!, "--list-sdks", cancellationToken);
        if (result.Success)
        {
            var lines = ParseSdks(result.StandardOutput);
            foreach (var line in lines)
            {
                yield return new DiscoveredApp(this, $".NET {line.Name}",
                    new AppIdentifier(Name, DisplayName, "Sdk"),
                    AppKind.DevTool)
                {
                    InstalledVersion = line.Version,
                    Path = Path.Combine(line.Path, line.Version),
                    Attribute = AppAttribute.DevTool | AppAttribute.Sdk,
                };
            }
        }
        else
        {
            logger.LogWarning("'dotnet --list-sdks' failed: {Err}", result.StandardError.Trim());
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateGlobalTools([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(_executablePath!, "tool list -g", cancellationToken);
        if (result.Success)
        {
            var lines = ParseGlobalTools(result.StandardOutput);
            foreach (var line in lines)
            {
                yield return new DiscoveredApp(this, line.Name,
                    new AppIdentifier(Name, DisplayName, "Global Tool"),
                    AppKind.DevTool)
                {
                    InstalledVersion = line.Version,
                    Attribute = AppAttribute.DevTool,
                };
            }
        }
        else
        {
            logger.LogWarning("'dotnet tool list -g' failed: {Err}", result.StandardError.Trim());
        }
    }

    /// <summary>
    /// Checks the latest version of a global tool from the NuGet registry and writes the result to the channel.
    /// </summary>
    private async Task CheckNuGetVersionAsync(AppRecord record, ChannelWriter<(AppRecord Record, bool Error)> writer, CancellationToken cancellationToken)
    {
        try
        {
            var latest = await FetchLatestNuGetVersionAsync(record.App.Name, cancellationToken).ConfigureAwait(false);
            if (latest is not null)
            {
                record.App.LatestVersion = latest;
            }

            await writer.WriteAsync((record, false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "NuGet version check failed for {Package}", record.App.Name);
            await writer.WriteAsync((record, true), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fetches the latest stable version of a NuGet package from the flat container API.
    /// Uses deduplication to avoid redundant requests for the same package.
    /// </summary>
    private Task<string?> FetchLatestNuGetVersionAsync(string name, CancellationToken cancellationToken)
    {
        return _inflightNuget.GetOrAdd(name, id => FetchNuGetVersionCoreAsync(id, cancellationToken));
    }

    private async Task<string?> FetchNuGetVersionCoreAsync(string name, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("nuget");
        var lowerId = name.ToLowerInvariant();
        var url = $"/v3-flatcontainer/{lowerId}/index.json";

        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogDebug("NuGet returned {Status} for {Package}",
                response.StatusCode,
                name);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var index = await JsonSerializer.DeserializeAsync(stream, DotnetJsonContext.Default.NugetVersionIndex, cancellationToken).ConfigureAwait(false);

        return SelectLatestStableVersion(index?.Versions);
    }

    /// <summary>
    /// Parses <c>dotnet --list-runtimes</c> output into runtime entries, grouping by the
    /// <c>name major.minor</c> key and keeping only the latest version in each group.
    /// </summary>
    internal static DotnetComponent[] ParseRuntimes(string output)
    {
        return output.Trim()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c =>
            {
                var parts = c.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                var name = parts[0];
                var version = parts[1];
                var path = parts[2].Trim('[', ']');
                return new DotnetComponent($"{name} {MajorMinor(version)}", version, path);
            })
            .GroupBy(l => l.Name) // Group by runtime name (e.g. "Microsoft.AspNetCore.App") to avoid duplicates when multiple versions are installed
            .Select(g => g.OrderByDescending(c => c.Version, VersionComparer.Instance).First()) // Take the latest version in each group
            .ToArray();
    }

    /// <summary>
    /// Parses <c>dotnet --list-sdks</c> output into SDK entries named by their <c>major.minor</c>
    /// channel, keeping only the latest version installed in each channel.
    /// </summary>
    internal static DotnetComponent[] ParseSdks(string output)
    {
        return output.Trim()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c =>
            {
                var parts = c.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var version = parts[0];
                var path = parts[1].Trim('[', ']');
                return new DotnetComponent(MajorMinor(version), version, path);
            })
            .GroupBy(l => l.Name)
            .Select(g => g.OrderByDescending(c => c.Version, VersionComparer.Instance).First()) // Take the latest version in each group
            .ToArray();
    }

    /// <summary>
    /// Parses <c>dotnet tool list -g</c> output into global tool entries, skipping the two
    /// header lines. The <c>Path</c> field carries the tool's invocation command.
    /// </summary>
    internal static DotnetComponent[] ParseGlobalTools(string output)
    {
        return output.Trim()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(2) // Skip header line
            .Select(c =>
            {
                var parts = c.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                return new DotnetComponent(parts[0], parts[1], parts[2]);
            })
            .ToArray();
    }

    /// <summary>
    /// Returns the latest stable (non-prerelease) version from a NuGet flat-container version
    /// list, which is sorted oldest-first. Falls back to the last entry when every version is a
    /// prerelease, and returns <see langword="null"/> when the list is null or empty.
    /// </summary>
    internal static string? SelectLatestStableVersion(string[]? versions)
    {
        if (versions is null or { Length: 0 })
        {
            return null;
        }

        for (var i = versions.Length - 1; i >= 0; i--)
        {
            var v = versions[i];
            if (!v.Contains('-', StringComparison.Ordinal))
            {
                return v;
            }
        }

        return versions[^1];
    }

    /// <summary>
    /// Returns the <c>major.minor</c> segment of a version string so that different
    /// installed SDK generations (e.g. 6.0 and 10.0) get unique names and are not
    /// collapsed by the name-based deduplication in <c>--show-all</c>.
    /// </summary>
    private static string MajorMinor(string version)
    {
        var firstDot = version.IndexOf('.');
        if (firstDot < 0)
        {
            return version;
        }

        var secondDot = version.IndexOf('.', firstDot + 1);
        return secondDot > 0 ? version[..secondDot] : version;
    }
}