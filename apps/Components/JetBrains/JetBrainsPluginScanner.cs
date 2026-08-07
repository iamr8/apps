using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Xml;

using Microsoft.Extensions.Logging;

namespace apps.Components.JetBrains;

/// <summary>
/// Discovers JetBrains IDE plugins installed at
/// <c>~/Library/Application Support/JetBrains/{Product}{Version}/plugins/</c>.
/// Handles both extracted-directory plugins (<c>META-INF/plugin.xml</c> on disk)
/// and JAR-format plugins (<c>lib/*.jar</c> containing <c>META-INF/plugin.xml</c>).
/// One <see cref="AppKind.Extension"/> entry is emitted per unique plugin ID.
/// </summary>
public sealed class JetBrainsPluginScanner(IHttpClientFactory httpClientFactory, ILogger<JetBrainsPluginScanner> logger)
    : IScanner
{
    private string[] _executablePaths = [];
    private Dictionary<string, string>? _builds;

    public string Name => "JetBrains";

    /// <inheritdoc/>
    public string DisplayName => "JetBrains";

    public OS SupportedOS => OS.MacOS | OS.Windows;
    public AppKind Kind => AppKind.Extension;

    public bool IsAvailable()
    {
        var root = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "JetBrains")
            : OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "JetBrains")
                : null;
        if (root is null)
        {
            return false;
        }

        if (!Directory.Exists(root))
        {
            return false;
        }

        try
        {
            _executablePaths = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly);
            return _executablePaths.Length > 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cannot list JetBrains product directories in {Root}", root);
            return false;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Track seen plugin IDs across all product dirs to avoid duplicates
        // (same plugin installed in Rider 2024.1 and 2024.3 should appear once).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var productDir in _executablePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pluginsDir = Path.Combine(productDir, "plugins");
            if (!Directory.Exists(pluginsDir))
            {
                continue;
            }

            string[] pluginDirs;
            try
            {
                pluginDirs = Directory.GetDirectories(pluginsDir, "*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var pluginDir in pluginDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DiscoveredApp? app = null;
                try
                {
                    app = await TryReadPluginAsync(pluginDir, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to read plugin at {Path}", pluginDir);
                }

                if (app is null || !seen.Add(app.UpdateInfo ?? app.Name))
                {
                    continue;
                }

                app.OwnerId = Path.GetFileName(productDir);
                yield return app;
            }
        }
    }

    public async IAsyncEnumerable<(AppRecord App, bool Error)> CheckAsync(AppRecord[] apps, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (apps.Length == 0)
        {
            yield break;
        }

        var builds = _builds ??= JetBrainsBuildResolver.ResolveBuilds();

        // Group plugins by their owning IDE's build so each build is queried once, constrained to
        // versions actually compatible with the installed IDE. Plugins whose build we can't resolve
        // fall back to an unconstrained per-plugin lookup.
        var byBuild = new Dictionary<string, List<AppRecord>>(StringComparer.OrdinalIgnoreCase);
        var noBuild = new List<AppRecord>();

        foreach (var record in apps)
        {
            if (record.App.OwnerId is { } dataDir && builds.TryGetValue(dataDir, out var build))
            {
                if (!byBuild.TryGetValue(build, out var list))
                {
                    list = [];
                    byBuild[build] = list;
                }

                list.Add(record);
            }
            else
            {
                noBuild.Add(record);
            }
        }

        foreach (var (build, records) in byBuild)
        {
            await foreach (var item in CheckCompatibleAsync(build, records, cancellationToken))
            {
                yield return item;
            }
        }

        if (noBuild.Count > 0)
        {
            await foreach (var item in noBuild.WhenAll<AppRecord, (AppRecord Record, bool Error)>(CheckPluginVersionAsync, cancellationToken: cancellationToken))
            {
                yield return item;
            }
        }
    }

    /// <summary>
    /// Queries the marketplace <c>compatibleUpdates</c> endpoint once for a whole IDE build,
    /// setting each plugin's latest version to the newest one compatible with that build.
    /// Plugins with no compatible newer version are left untouched (reported as up to date).
    /// </summary>
    private async IAsyncEnumerable<(AppRecord App, bool Error)> CheckCompatibleAsync(
        string build,
        List<AppRecord> records,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var xmlIds = records
            .Select(r => r.App.UpdateInfo)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Dictionary<string, string>? latestByXmlId = null;
        var failed = false;

        if (xmlIds.Length > 0)
        {
            try
            {
                using var client = httpClientFactory.CreateClient("jetbrains");
                var request = new JetBrainsCompatibleUpdateRequest { Build = build, PluginXmlIds = xmlIds! };
                using var content = JsonContent.Create(request, JetBrainsJsonContext.Default.JetBrainsCompatibleUpdateRequest);
                using var response = await client.PostAsync("/api/search/compatibleUpdates", content, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var updates = await response.Content
                    .ReadFromJsonAsync(JetBrainsJsonContext.Default.JetBrainsCompatibleUpdateArray, cancellationToken)
                    .ConfigureAwait(false);

                latestByXmlId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (updates is not null)
                {
                    foreach (var update in updates.Where(u => u is { PluginXmlId.Length: > 0, Version.Length: > 0 }))
                    {
                        latestByXmlId[update.PluginXmlId!] = update.Version!;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "JetBrains compatibleUpdates check failed for build {Build}", build);
                failed = true;
            }
        }

        foreach (var record in records)
        {
            if (failed)
            {
                yield return (record, true);
                continue;
            }

            if (record.App.UpdateInfo is { } xmlId
                && latestByXmlId is not null
                && latestByXmlId.TryGetValue(xmlId, out var version))
            {
                record.App.LatestVersion = version;
            }
            else
            {
                // No build-compatible version listed on the marketplace (unlisted/internal plugin) —
                // we can't confirm its update status, so mark it unchecked rather than implying it's current.
                record.CheckFailed = true;
            }

            yield return (record, false);
        }
    }

    /// <summary>
    /// Queries the JetBrains plugin repository for the latest version of a single plugin.
    /// Resolves string XML IDs to numeric IDs when needed.
    /// </summary>
    private async Task CheckPluginVersionAsync(AppRecord record, ChannelWriter<(AppRecord Record, bool Error)> writer, CancellationToken cancellationToken)
    {
        var xmlId = record.App.UpdateInfo;
        if (string.IsNullOrWhiteSpace(xmlId))
        {
            await writer.WriteAsync((record, false), cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            using var client = httpClientFactory.CreateClient("jetbrains");

            string numericId;
            if (IsNumeric(xmlId))
            {
                numericId = xmlId;
            }
            else
            {
                var resolved = await ResolveNumericIdAsync(client, xmlId, cancellationToken).ConfigureAwait(false);
                if (resolved is null)
                {
                    await writer.WriteAsync((record, false), cancellationToken).ConfigureAwait(false);
                    return;
                }

                numericId = resolved;
            }

            var updates = await client
                .GetFromJsonAsync(
                    $"/api/plugins/{numericId}/updates?channel=&size=1",
                    JetBrainsJsonContext.Default.JetBrainsPluginUpdateArray,
                    cancellationToken)
                .ConfigureAwait(false);

            var latest = updates?.FirstOrDefault()?.Version;
            if (!string.IsNullOrWhiteSpace(latest))
            {
                record.App.LatestVersion = latest;
            }

            await writer.WriteAsync((record, false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "JetBrains plugin check failed for {Name} (id={Id})",
                record.App.Name,
                xmlId);
            await writer.WriteAsync((record, true), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Searches the JetBrains plugin repository by XML ID and returns the numeric plugin ID,
    /// or <see langword="null"/> when the plugin is not publicly listed.
    /// </summary>
    private async Task<string?> ResolveNumericIdAsync(HttpClient client, string xmlId, CancellationToken cancellationToken)
    {
        using var response = await client
            .GetAsync($"/api/plugins?xmlId={Uri.EscapeDataString(xmlId)}&family=intellij&size=1", cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var searchResult = await response.Content
            .ReadFromJsonAsync(JetBrainsJsonContext.Default.JetBrainsPluginInfo, cancellationToken)
            .ConfigureAwait(false);

        var id = searchResult?.Id;
        return id.HasValue ? id.Value.ToString() : null;
    }

    private static bool IsNumeric(string value)
    {
        foreach (var ch in value)
        {
            if (!char.IsAsciiDigit(ch))
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    /// <summary>
    /// Attempts to read plugin metadata from a plugin directory.
    /// Tries <c>META-INF/plugin.xml</c> directly first, then scans <c>lib/*.jar</c>.
    /// </summary>
    private async Task<DiscoveredApp?> TryReadPluginAsync(string pluginDir, CancellationToken cancellationToken)
    {
        // Case 1: extracted plugin — META-INF/plugin.xml on disk
        var xmlPath = Path.Combine(pluginDir, "META-INF", "plugin.xml");
        if (File.Exists(xmlPath))
        {
            return await ParsePluginXmlAsync(xmlPath, xmlPath, cancellationToken);
        }

        // Case 2: JAR-format plugin — lib/*.jar with embedded META-INF/plugin.xml
        var libDir = Path.Combine(pluginDir, "lib");
        if (!Directory.Exists(libDir))
        {
            return null;
        }

        foreach (var jar in Directory.EnumerateFiles(libDir, "*.jar"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var app = TryReadPluginFromJar(jar);
            if (app is not null)
            {
                return app;
            }
        }

        return null;
    }

    private DiscoveredApp? TryReadPluginFromJar(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var entry = zip.GetEntry("META-INF/plugin.xml");
            if (entry is null)
            {
                return null;
            }

            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var xml = reader.ReadToEnd();
            return ParsePluginXmlString(xml, jarPath);
        }
        catch
        {
            return null;
        }
    }

    private async Task<DiscoveredApp?> ParsePluginXmlAsync(string xmlPath, string sourcePath, CancellationToken ct)
    {
        var xml = await File.ReadAllTextAsync(xmlPath, ct);
        return ParsePluginXmlString(xml, sourcePath);
    }

    private DiscoveredApp? ParsePluginXmlString(string xml, string sourcePath)
    {
        if (ParsePluginXml(xml) is not var (id, name, version, displayId) || displayId is null)
        {
            return null;
        }

        return new DiscoveredApp(this,
            name ?? displayId,
            new AppIdentifier(Name, DisplayName, "Plugin"),
            AppKind.Extension)
        {
            PackageId = id,
            InstalledVersion = version,
            Path = sourcePath,
            Attribute = AppAttribute.JetBrainsPlugin,
            UpdateInfo = displayId,
        };
    }

    /// <summary>
    /// Parses the <c>id</c>, <c>name</c>, and <c>version</c> elements out of a JetBrains
    /// <c>plugin.xml</c> descriptor.
    /// </summary>
    /// <param name="xml">The raw <c>plugin.xml</c> content.</param>
    /// <returns>
    /// A tuple of the trimmed identity fields plus the resolved <c>DisplayId</c> (the <c>id</c> when
    /// present, otherwise the <c>name</c>), or <see langword="null"/> when the XML has no root element
    /// or carries neither an <c>id</c> nor a <c>name</c>.
    /// </returns>
    /// <exception cref="XmlException">The XML is not well-formed.</exception>
    internal static (string? Id, string? Name, string? Version, string? DisplayId)? ParsePluginXml(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        var root = doc.DocumentElement;
        if (root is null)
        {
            return null;
        }

        var id = root.SelectSingleNode("id")?.InnerText?.Trim();
        var name = root.SelectSingleNode("name")?.InnerText?.Trim();
        var version = root.SelectSingleNode("version")?.InnerText?.Trim();

        var displayId = id ?? name;
        if (string.IsNullOrWhiteSpace(displayId))
        {
            return null;
        }

        return (id, name, version, displayId);
    }
}