using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Xml;

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
public sealed class ChromeExtScanner(IHttpClientFactory httpClientFactory, ILogger<ChromeExtScanner> logger)
    : IScanner
{
    private string? _executablePath;

    public string Name => "ChromeExt";

    /// <inheritdoc/>
    public string DisplayName => "Chrome";

    /// <inheritdoc/>
    public string ProgressLabel => "Chrome Extensions";

    /// <inheritdoc/>
    public string ProgressItemNoun => "extension";

    public OS SupportedOS => OS.MacOS | OS.Windows;
    public AppKind Kind => AppKind.Extension;


    public bool IsAvailable()
    {
        // { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "Chrome Apps.localized"), false },
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

    public async IAsyncEnumerable<(AppRecord App, bool Error)> CheckAsync(AppRecord[] apps, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (apps.Length == 0)
        {
            yield break;
        }

        await foreach (var item in apps.WhenAll<AppRecord, (AppRecord Record, bool Error)>(CheckExtensionVersionAsync, cancellationToken: cancellationToken))
        {
            yield return item;
        }
    }

    /// <summary>
    /// Queries Chrome's CRX update protocol for the latest version of a single extension.
    /// </summary>
    private async Task CheckExtensionVersionAsync(AppRecord record, ChannelWriter<(AppRecord Record, bool Error)> writer, CancellationToken cancellationToken)
    {
        try
        {
            var extensionId = record.App.PackageId;
            if (string.IsNullOrWhiteSpace(extensionId))
            {
                return;
            }

            using var client = httpClientFactory.CreateClient("chrome-update");
            var url = BuildUpdateCheckUrl(extensionId);

            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("Chrome update endpoint returned {Status} for {ExtId}", response.StatusCode, extensionId);
                return;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var version = ParseUpdateCheckVersion(content);
                if (!string.IsNullOrWhiteSpace(version))
                {
                    logger.LogDebug("Chrome extension {ExtId} has latest version {Version}", extensionId, version);
                    record.App.LatestVersion = version;
                }

                await writer.WriteAsync((record, false), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to parse Chrome update XML for {ExtId}", extensionId);
                await writer.WriteAsync((record, false), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Chrome Web Store version check failed for {Extension}", record.App.Name);
            await writer.WriteAsync((record, true), cancellationToken).ConfigureAwait(false);
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

                var versionDirs = SafeEnumerateDirectories(extIdDir);
                // Chrome uses version strings as folder names; sort lexicographically descending to get the latest
                var versionDir = versionDirs
                    .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (versionDir is null)
                {
                    continue;
                }

                var manifestPath = Path.Combine(versionDir, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                ChromeManifest? manifest;
                try
                {
                    await using var stream = File.OpenRead(manifestPath);
                    manifest = await JsonSerializer.DeserializeAsync(stream, ChromeJsonContext.Default.ChromeManifest, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to read manifest for Chrome extension {Id}", extId);
                    continue;
                }

                if (ProjectManifest(manifest) is not { } projected)
                {
                    continue;
                }

                var (name, version, description) = projected;

                logger.LogDebug(
                    "Discovered Chrome extension {Name} v{Version} [{Id}]",
                    name, version ?? "?", extId);

                yield return new DiscoveredApp(this, name,
                    new AppIdentifier(Name, DisplayName, "Extension"),
                    AppKind.Extension)
                {
                    Path = versionDir,
                    Description = description,
                    InstalledVersion = version,
                    PackageId = extId,
                    Attribute = AppAttribute.ChromeExtension,
                    UpdateInfo = extId,
                };
            }
        }
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

    /// <summary>
    /// Builds the relative CRX update-check URL for a single extension id.
    /// </summary>
    /// <param name="extensionId">The Chrome extension id to query.</param>
    /// <returns>The relative request path for Chrome's <c>update2</c> endpoint.</returns>
    internal static string BuildUpdateCheckUrl(string extensionId) =>
        $"/service/update2/crx?response=updatecheck&acceptformat=crx3&prodversion=130.0&x=id%3D{extensionId}%26uc";

    /// <summary>
    /// Parses Chrome's CRX update-check XML response and returns the advertised latest version.
    /// </summary>
    /// <param name="xml">The raw XML body returned by the update endpoint.</param>
    /// <returns>
    /// The latest version string, or <see langword="null"/> when the response carries no
    /// <c>updatecheck</c> node, reports <c>noupdate</c>, or omits a version attribute.
    /// </returns>
    internal static string? ParseUpdateCheckVersion(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        var nsMgr = new XmlNamespaceManager(doc.NameTable);
        nsMgr.AddNamespace("g", "http://www.google.com/update2/response");

        var updateCheck = doc.SelectSingleNode("//g:app/g:updatecheck", nsMgr);
        if (updateCheck is null)
        {
            return null;
        }

        var status = updateCheck.Attributes?["status"]?.Value;
        if (string.Equals(status, "noupdate", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var version = updateCheck.Attributes?["version"]?.Value;
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    /// <summary>
    /// Projects a parsed Chrome manifest into the display fields used for discovery, applying the
    /// rule that skips extensions with a missing name or a synthetic localized name (e.g.
    /// <c>__MSG_appName__</c>).
    /// </summary>
    /// <param name="manifest">The deserialized manifest, or <see langword="null"/>.</param>
    /// <returns>
    /// The trimmed name, version, and description, or <see langword="null"/> when the extension
    /// should be skipped.
    /// </returns>
    internal static (string Name, string? Version, string? Description)? ProjectManifest(ChromeManifest? manifest)
    {
        var name = manifest?.Name?.Trim();

        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("__", StringComparison.Ordinal))
        {
            return null;
        }

        return (name, manifest?.Version?.Trim(), manifest?.Description?.Trim());
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

    [JsonPropertyName("update_url")]
    public string? UpdateUrl { get; init; }
}

[JsonSerializable(typeof(ChromeManifest))]
internal sealed partial class ChromeJsonContext : JsonSerializerContext;