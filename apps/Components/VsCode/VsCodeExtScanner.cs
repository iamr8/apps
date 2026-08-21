using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

namespace apps.Components.VsCode;

/// <summary>
/// Discovers VS Code extensions via <c>code --list-extensions --show-versions</c>.
/// Each extension is emitted as <see cref="AppKind.Extension"/>;
/// the marketplace display name is resolved from the local <c>package.json</c>
/// and stored in <see cref="DiscoveredApp.Description"/> for two-line display.
/// </summary>
public sealed class VsCodeExtScanner(IProcessRunner runner, IHttpClientFactory httpClientFactory, ILogger<VsCodeExtScanner> logger)
    : IScanner
{
    private string? _executablePath;

    public string Name => "VSCode";

    /// <inheritdoc/>
    public string DisplayName => "VS Code";

    /// <inheritdoc/>
    public string ProgressLabel => "VS Code Extensions";

    /// <inheritdoc/>
    public string ProgressItemNoun => "extension";

    public OS SupportedOS => OS.MacOS | OS.Windows;
    public AppKind Kind => AppKind.Extension;

    private static readonly string ExtensionsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");

    /// <inheritdoc/>
    public bool IsAvailable()
    {
        _executablePath = OperatingSystem.IsWindows()
            ? ScannerHelper.FindExecutable("code.cmd")
            : ScannerHelper.FindExecutable("code");
        return _executablePath is not null;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(_executablePath!, "--list-extensions --show-versions", cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning("'code --list-extensions' failed: {Err}", result.StandardError.Trim());
            yield break;
        }

        var displayNames = BuildDisplayNameIndex();

        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var app = ParseLine(line, displayNames);
            if (app is not null)
            {
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

        await foreach (var (record, error) in apps.Chunk(20).WhenAll<AppRecord[], (AppRecord App, bool Error)>(FetchBatchAsync, cancellationToken: cancellationToken))
        {
            yield return (record, error);
        }
    }

    /// <summary>
    /// Posts a single batch of extension IDs to the Marketplace gallery API and writes
    /// resolved versions to the channel.
    /// </summary>
    private async Task FetchBatchAsync(AppRecord[] records, ChannelWriter<(AppRecord App, bool Error)> writer, CancellationToken cancellationToken)
    {
        var body = BuildQueryRequest(records);

        try
        {
            using var client = httpClientFactory.CreateClient("vscode");
            var requestJson = JsonSerializer.Serialize(body, VsCodeJsonContext.Default.VsCodeQueryRequest);
            var stringContent = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(GalleryApiPath, stringContent, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                logger.LogWarning("VS Code Marketplace API returned {Status} for batch of {Count} extensions", response.StatusCode, records.Length);
                foreach (var record in records)
                {
                    await writer.WriteAsync((record, true), cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var queryResult = await JsonSerializer.DeserializeAsync(stream, VsCodeJsonContext.Default.VsCodeQueryResponse, cancellationToken).ConfigureAwait(false);
            var extensions = queryResult?.Results?.FirstOrDefault()?.Extensions;
            if (extensions is null)
            {
                return;
            }

            foreach (var ext in extensions)
            {
                var publisherName = ext.Publisher?.PublisherName;
                var extensionName = ext.ExtensionName;
                var version = GetLatestStableVersion(ext.Versions);

                if (string.IsNullOrWhiteSpace(publisherName) || string.IsNullOrWhiteSpace(extensionName) || string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                var compositeId = $"{publisherName}.{extensionName}";
                var record = records.First(r => string.Equals(r.App.PackageId, compositeId, StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(r.App.Name, compositeId, StringComparison.OrdinalIgnoreCase));
                record.App.LatestVersion = version;
                await writer.WriteAsync((record, false)!, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "VS Code Marketplace batch fetch failed for {Count} extensions", records.Length);
            foreach (var record in records)
            {
                await writer.WriteAsync((record, true)!, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Returns the newest non-pre-release version from the gallery <paramref name="versions"/>
    /// list, falling back to the first entry when every version is a pre-release. Returns
    /// <see langword="null"/> when <paramref name="versions"/> is <see langword="null"/> or empty.
    /// </summary>
    internal static string? GetLatestStableVersion(VsCodeExtVersion[]? versions)
    {
        if (versions is null)
        {
            return null;
        }

        foreach (var v in versions)
        {
            if (!IsPreRelease(v))
            {
                return v.Version;
            }
        }

        return versions.FirstOrDefault()?.Version;
    }

    private static bool IsPreRelease(VsCodeExtVersion version)
    {
        if (version.Properties is null)
        {
            return false;
        }

        foreach (var prop in version.Properties)
        {
            if (string.Equals(prop.Key, PreReleasePropertyKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(prop.Value, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the Marketplace gallery query body for a batch of <paramref name="records"/>,
    /// adding one extension-name criterion per record (preferring <see cref="DiscoveredApp.PackageId"/>,
    /// falling back to <see cref="DiscoveredApp.Name"/>).
    /// </summary>
    internal static VsCodeQueryRequest BuildQueryRequest(AppRecord[] records)
    {
        var criteria = records
            .Select(c => c.App.PackageId ?? c.App.Name)
            .Select(id => new VsCodeCriterion { FilterType = FilterTypeExtensionName, Value = id })
            .ToArray();

        return new VsCodeQueryRequest
        {
            Filters =
            [
                new VsCodeFilter
                {
                    Criteria = criteria,
                    PageSize = records.Length,
                    PageNumber = 1
                }
            ],
            Flags = QueryFlags
        };
    }

    private const string GalleryApiPath = "/_apis/public/gallery/extensionquery?api-version=7.2-preview.1";
    private const int FilterTypeExtensionName = 7;
    private const int QueryFlags = 1 | 16;
    private const string PreReleasePropertyKey = "Microsoft.VisualStudio.Code.PreRelease";

    /// <summary>
    /// Builds a dictionary from extension ID (lower-cased) to its marketplace display name
    /// by scanning the local <c>~/.vscode/extensions/</c> directory.
    /// </summary>
    private Dictionary<string, string> BuildDisplayNameIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(ExtensionsRoot))
        {
            return index;
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(ExtensionsRoot))
            {
                var folderName = Path.GetFileName(dir);

                // Skip hidden directories VS Code uses for staging/temp (e.g. .ebf6263f-…);
                // real extension folders always follow the {publisher}.{name}-{version} format.
                if (folderName.StartsWith('.'))
                {
                    continue;
                }

                var pkgPath = Path.Combine(dir, "package.json");
                if (!File.Exists(pkgPath))
                {
                    continue;
                }

                // Folder name format: {publisher}.{name}-{version}
                var hyphen = folderName.LastIndexOf('-');
                if (hyphen <= 0)
                {
                    continue;
                }

                var extensionId = folderName[..hyphen];
                if (string.IsNullOrWhiteSpace(extensionId) || index.ContainsKey(extensionId))
                {
                    continue;
                }

                try
                {
                    using var stream = File.OpenRead(pkgPath);
                    var pkg = JsonSerializer.Deserialize(stream, VsCodeJsonContext.Default.VsCodePackageJson);
                    var displayName = pkg?.DisplayName?.Trim();
                    if (!string.IsNullOrEmpty(displayName) && displayName != extensionId && !IsNlsPlaceholder(displayName))
                    {
                        index[extensionId] = displayName;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not read package.json at {Path}", pkgPath);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Cannot enumerate VS Code extensions directory: {Root}", ExtensionsRoot);
        }

        return index;
    }

    /// <summary>
    /// Line format: <c>publisher.extension-id@1.2.3</c>
    /// </summary>
    private DiscoveredApp? ParseLine(string line, Dictionary<string, string> displayNames)
    {
        if (ParseExtensionLine(line) is not (var extensionId, var version))
        {
            return null;
        }

        displayNames.TryGetValue(extensionId, out var displayName);

        return new DiscoveredApp(this,
            displayName ?? extensionId,
            new AppIdentifier(Name, DisplayName, "Extension"),
            AppKind.Extension)
        {
            PackageId = extensionId,
            InstalledVersion = version,
            Path = Path.Combine(ExtensionsRoot, extensionId),
            UpdateInfo = extensionId,
            Attribute = AppAttribute.VsCodeExtension
        };
    }

    /// <summary>
    /// True when a package.json <c>displayName</c> is an unresolved NLS localization placeholder
    /// such as <c>%displayName%</c> — a key into <c>package.nls.json</c> that the manifest never
    /// inlined. Such values are rejected so the extension id is shown as the name instead.
    /// </summary>
    internal static bool IsNlsPlaceholder(string value)
        => value.Length >= 2 && value[0] == '%' && value[^1] == '%';

    /// <summary>
    /// Splits a <c>code --list-extensions --show-versions</c> line of the form
    /// <c>publisher.extension-id@1.2.3</c> into its extension ID and version, using the last
    /// <c>@</c> as the separator. Returns <see langword="null"/> when there is no <c>@</c> or
    /// the extension ID is blank.
    /// </summary>
    internal static (string ExtensionId, string Version)? ParseExtensionLine(string line)
    {
        var atIdx = line.LastIndexOf('@');
        if (atIdx < 0)
        {
            return null;
        }

        var extensionId = line[..atIdx];
        var version = line[(atIdx + 1)..];

        if (string.IsNullOrWhiteSpace(extensionId))
        {
            return null;
        }

        return (extensionId, version);
    }
}