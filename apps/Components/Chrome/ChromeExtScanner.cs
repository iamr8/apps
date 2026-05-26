using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using apps.Models;
using apps.Scanners;

using Microsoft.Extensions.Logging;

namespace apps.Components.Chrome;

/// <summary>
/// Discovers Google Chrome (and Chrome Canary) extensions by reading each profile's
/// <c>Extensions/{id}/{version}/manifest.json</c>.
/// Extensions are emitted as <see cref="AppKind.Extension"/> with
/// <see cref="UpdateMethod.SelfUpdate"/> — Chrome auto-updates all extensions silently
/// via the CRX update protocol; no external check is needed.
/// Duplicate extension IDs across profiles are emitted only once.
/// </summary>
public sealed class ChromeExtScanner(ILogger<ChromeExtScanner> logger)
    : IScanner
{
    private string? _executablePath;

    public string Name => "ChromeExt";

    /// <inheritdoc/>
    public string DisplayName => "Chrome";

    public OS SupportedOS => OS.MacOS | OS.Windows;

    /// <inheritdoc/>
    /// <remarks>All apps from this scanner are extensions; the qualifier is always "Extension".</remarks>
    public string? GetSourceQualifier(AppKind kind) => "Extension";

    public bool IsAvailable()
    {
        var chrome = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Google", "Chrome")
            : OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Google", "Chrome")
                : null;
        if (chrome is null)
        {
            return false;
        }

        if (Directory.Exists(chrome))
        {
            _executablePath = chrome;
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var app in ScanChromeRootAsync(_executablePath!, seen, cancellationToken).ConfigureAwait(false))
        {
            yield return app;
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> ScanChromeRootAsync(string chromeRoot, HashSet<string> seen, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var profileDir in EnumerateProfileDirs(chromeRoot))
        {
            var extensionsDir = Path.Combine(profileDir, "Extensions");
            if (!Directory.Exists(extensionsDir))
            {
                continue;
            }

            foreach (var extIdDir in SafeEnumerateDirectories(extensionsDir))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var extId = Path.GetFileName(extIdDir);
                if (!seen.Add(extId))
                {
                    continue;
                }

                var app = await ReadExtensionAsync(extId, extIdDir, cancellationToken).ConfigureAwait(false);
                if (app is not null)
                {
                    yield return app;
                }
            }
        }
    }

    private async Task<DiscoveredApp?> ReadExtensionAsync(
        string extId,
        string extIdDir,
        CancellationToken cancellationToken)
    {
        var versionDirs = SafeEnumerateDirectories(extIdDir);
        // Chrome uses version strings as folder names; sort lexicographically descending to get the latest
        var versionDir = versionDirs
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (versionDir is null)
        {
            return null;
        }

        var manifestPath = Path.Combine(versionDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        ChromeManifest? manifest;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            manifest = await JsonSerializer
                .DeserializeAsync(stream, ChromeJsonContext.Default.ChromeManifest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read manifest for Chrome extension {Id}", extId);
            return null;
        }

        var name = manifest?.Name?.Trim();

        // Chrome internal/component extensions use synthetic names like "__MSG_appName__"
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("__", StringComparison.Ordinal))
        {
            return null;
        }

        var version = manifest?.Version?.Trim();
        var description = manifest?.Description?.Trim();

        logger.LogDebug(
            "Discovered Chrome extension {Name} v{Version} [{Id}]",
            name, version ?? "?", extId);

        return new DiscoveredApp(
            name,
            new AppIdentifier(Name, DisplayName, "Extension"),
            AppKind.Extension,
            version,
            versionDir,
            SuggestedMethod: UpdateMethod.SelfUpdate,
            SuggestedMethodDetail: extId,
            Description: string.IsNullOrWhiteSpace(description) ? null : description);
    }

    private List<string> EnumerateProfileDirs(string chromeRoot)
    {
        var profiles = new List<string>();

        var defaultProfile = Path.Combine(chromeRoot, "Default");
        if (Directory.Exists(defaultProfile))
        {
            profiles.Add(defaultProfile);
        }

        foreach (var dir in SafeEnumerateDirectories(chromeRoot))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
            {
                profiles.Add(dir);
            }
        }

        return profiles;
    }

    private IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Cannot enumerate directory: {Path}", path);
            return [];
        }
    }
}

internal sealed class ChromeManifest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

[JsonSerializable(typeof(ChromeManifest))]
internal sealed partial class ChromeJsonContext : JsonSerializerContext;