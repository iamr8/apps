using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Xml;

using Microsoft.Extensions.Logging;

namespace apps.Components.MacOs;

/// <summary>
/// Discovers GUI .app bundles by walking well-known macOS application directories at depth 1.
/// Reads each bundle's Info.plist to extract name, version, bundle ID, and Sparkle feed URL.
///
/// Apple / OS system apps — bundles whose <c>CFBundleIdentifier</c> starts with
/// <c>com.apple.</c>, or any bundle under <c>/System/Applications</c> — are tagged
/// <see cref="AppKind.SystemApp"/> with <c>SuggestedMethod = None</c> so they are
/// tracked for inventory but never subjected to update checks.
/// All other apps are tagged <see cref="AppKind.App"/>.
/// </summary>
public sealed partial class MacApplicationsScanner(
    PlistReader plistReader,
    IProcessRunner runner,
    IHttpClientFactory httpClientFactory,
    ILogger<MacApplicationsScanner> logger)
    : IScanner
{
    private Dictionary<string, bool> _appsExecutablePaths = [];
    private string? _brewExecutablePath;

    public string Name => "Application";

    /// <inheritdoc/>
    public string DisplayName => "Application";

    public OS SupportedOS => OS.MacOS;
    public AppKind Kind => AppKind.App | AppKind.Extension | AppKind.Package;

    private readonly ConcurrentDictionary<string, DiscoveredApp> _scannedApps = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public bool IsAvailable()
    {
        var scanRoots = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            { "/Applications", false },
            { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"), false },
            { "/Applications/Utilities", false },
            { "/System/Applications", true }
        };
        _appsExecutablePaths = scanRoots.Keys.Where(Directory.Exists).ToDictionary(path => path, path => scanRoots[path]);

        string[] candidates =
        [
            "/opt/homebrew/bin/brew",
            "/usr/local/bin/brew"
        ];
        _brewExecutablePath = candidates.FirstOrDefault(File.Exists);

        return _brewExecutablePath is not null || _appsExecutablePaths.Count > 0;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var app in EnumerateApps(cancellationToken))
        {
            _scannedApps[app.Name] = app;
            yield return app;
        }

        await foreach (var app in EnumerateHomebrew(cancellationToken))
        {
            yield return app;
        }

        await foreach (var app in EnumerateSoftwareUpdates(cancellationToken))
        {
            yield return app;
        }
    }

    public async IAsyncEnumerable<(AppRecord App, bool Error)> CheckAsync(AppRecord[] apps, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var resolvedApps = new List<AppRecord>();
        await foreach (var (app, resolved, error) in apps.WhenAll<AppRecord, (AppRecord App, bool Resolved, bool Error)>(onPublication: CheckAppAsync, cancellationToken: cancellationToken))
        {
            if (!resolved)
            {
                continue;
            }

            resolvedApps.Add(app);
            yield return (app, error);
        }

        var unresolvedApps = apps.Where(a => !resolvedApps.Any(r => r.App.Name.Equals(a.App.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        await foreach (var (app, resolved, error) in unresolvedApps.WhenAll<AppRecord, (AppRecord App, bool Resolved, bool Error)>(onPublication: CheckHomebrewAsync, cancellationToken: cancellationToken))
        {
            if (!resolved)
            {
                continue;
            }

            resolvedApps.Add(app);
            yield return (app, error);
        }

        unresolvedApps = apps.Except(resolvedApps).ToList();
        if (unresolvedApps.Count > 0)
        {
            foreach (var record in unresolvedApps)
            {
                logger.LogDebug("Failed to resolve update information for {AppName}, skipping", record.App.Name);
                yield return (record, false);
            }
        }
    }

    private async Task CheckHomebrewAsync(AppRecord record, ChannelWriter<(AppRecord App, bool Resolved, bool Error)> writer, CancellationToken cancellationToken)
    {
        try
        {
            var token = CreateToken(record.App.Name);
            var tuple = await GetLatestVersionByCaskAsync(token, record.App.Path, cancellationToken).ConfigureAwait(false);
            if (tuple is null)
            {
                logger.LogDebug("No Homebrew information found for {AppName} with token '{Token}'", record.App.Name, token);
                await writer.WriteAsync((record, false, false), cancellationToken).ConfigureAwait(false);
                return;
            }

            if (record.App.Description is null && tuple.Value.Description is not null)
            {
                record.App.Description = tuple.Value.Description; // enrich existing record with Homebrew API description if missing
            }

            record.App.LatestVersion = tuple.Value.LatestVersion;
            await writer.WriteAsync((record, true, false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await writer.WriteAsync((record, false, true), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CheckAppAsync(AppRecord record, ChannelWriter<(AppRecord App, bool Resolved, bool Error)> writer, CancellationToken cancellationToken)
    {
        if (record.App.Attribute.HasFlag(AppAttribute.HomebrewCask) || record.App.Attribute.HasFlag(AppAttribute.HomebrewFormula))
        {
            // Will be published on its method-specific check.
            logger.LogDebug("App {AppName} has suggested Homebrew update method, skipping iTunes lookup", record.App.Name);
            return;
        }

        if (record.App.LatestVersion is not null)
        {
            logger.LogDebug("App {AppName} is already updated to v{Version}", record.App.Name, record.App.InstalledVersion);
            await writer.WriteAsync((record, true, false), cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            if (record.App.Attribute.HasFlag(AppAttribute.SparkleFeed))
            {
                if (await GetLatestVersionBySparkleAsync(record, cancellationToken))
                {
                    await writer.WriteAsync((record, true, false), cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            else if (record.App.Attribute.HasFlag(AppAttribute.ElectronApp) && !record.App.Attribute.HasFlag(AppAttribute.AppStoreApp))
            {
                if (await GetLatestVersionByElectronAsync(record, cancellationToken))
                {
                    await writer.WriteAsync((record, true, false), cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            else if (record.App.Attribute.HasFlag(AppAttribute.AppStoreApp))
            {
                if (await GetLatestVersionByITunesAsync(record, cancellationToken))
                {
                    await writer.WriteAsync((record, true, false), cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                // This app is not an App Store/Sparkle/Electron app
                Console.WriteLine($"App {record.App.Name} has no identifiable update method (not Sparkle, Electron, or App Store), skipping");
            }

            logger.LogDebug("No update information found for {AppName}", record.App.Name);
            await writer.WriteAsync((record, false, false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await writer.WriteAsync((record, false, true), cancellationToken).ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateApps([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_appsExecutablePaths.Count == 0)
        {
            logger.LogWarning("No application directories found to scan");
            yield break;
        }

        foreach (var (root, rootIsSystem) in _appsExecutablePaths)
        {
            IEnumerable<string> appBundles;
            try
            {
                appBundles = Directory.EnumerateDirectories(root, "*.app", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cannot enumerate directory: {Root}", root);
                continue;
            }

            foreach (var bundlePath in appBundles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (rootIsSystem)
                {
                    continue;
                }

                var plist = await GetPlistInfo(bundlePath, cancellationToken);
                if (plist is null)
                {
                    logger.LogDebug("Skipping bundle with no plist: {Bundle}", bundlePath);
                    continue;
                }

                var name = Normalize(plist.DisplayName ?? Path.GetFileNameWithoutExtension(bundlePath));

                if (string.IsNullOrWhiteSpace(name))
                {
                    logger.LogDebug("Skipping bundle with no name: {Bundle}", bundlePath);
                    continue;
                }

                var version = Normalize(plist.ShortVersion ?? plist.BundleVersion)!;
                var buildVersion = Normalize(plist.BundleVersion)!;
                var bundleId = Normalize(plist.BundleIdentifier)!;

                var subApps = new List<DiscoveredApp>();
                var pluginsDir = Path.Combine(bundlePath, "Contents", "PlugIns");
                if (Directory.Exists(pluginsDir))
                {
                    string[] appexBundles = [];
                    try
                    {
                        appexBundles = Directory.EnumerateDirectories(pluginsDir, "*.appex", SearchOption.TopDirectoryOnly).ToArray();
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Cannot enumerate PlugIns directory: {Dir}", pluginsDir);
                    }

                    if (appexBundles.Length > 0)
                    {
                        foreach (var appexPath in appexBundles)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var appexPlist = await GetPlistInfo(appexPath, cancellationToken);
                            if (appexPlist is not null)
                            {
                                if (appexPlist.Attribute.HasFlag(AppAttribute.SafariExtension))
                                {
                                    plist = plist with
                                    {
                                        Attribute = plist.Attribute | AppAttribute.SafariExtension
                                    }; // propagate to parent app for easier detection
                                    break;
                                }
                            }
                        }
                    }
                }

                if (plist.Attribute.HasFlag(AppAttribute.ElectronApp) && !plist.Attribute.HasFlag(AppAttribute.AppStoreApp))
                {
                    var electronApp = await GetElectronApp(bundlePath, name, version, bundleId, subApps, plist, cancellationToken);
                    yield return electronApp with
                    {
                        SubApps = subApps,
                        Attribute = plist.Attribute
                    };
                    continue;
                }

                string? updateInfo = null;
                if (plist.Attribute.HasFlag(AppAttribute.SparkleFeed))
                {
                    updateInfo = plist.SparkleUrl;
                }

                logger.LogDebug(
                    "Discovered {Kind} {Name} v{Version} [{BundleId}] at {Path}",
                    AppKind.App, name, version, bundleId ?? "—", bundlePath);

                AppIdentifier appIdentifier;
                if (plist.Attribute.HasFlag(AppAttribute.PwaApp))
                {
                    appIdentifier = new AppIdentifier(Name, DisplayName, "PWA");
                }
                else if (plist.Attribute.HasFlag(AppAttribute.AppStoreApp))
                {
                    appIdentifier = new AppIdentifier(Name, DisplayName, "App Store");
                }
                else if (plist.Attribute.HasFlag(AppAttribute.SafariExtension))
                {
                    appIdentifier = new AppIdentifier("SafariExt", "Safari", "Extension");
                }
                else
                {
                    appIdentifier = new AppIdentifier(Name, DisplayName);
                }

                yield return new DiscoveredApp(this, name, appIdentifier, plist.Attribute.HasFlag(AppAttribute.SafariExtension) ? AppKind.Extension : AppKind.App)
                {
                    InstalledVersion = version,
                    InstalledBuildNumber = buildVersion,
                    BundleId = bundleId,
                    Path = bundlePath,
                    UpdateInfo = updateInfo,
                    SubApps = subApps,
                    Attribute = plist.Attribute
                };
            }
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateHomebrew([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_brewExecutablePath is null)
        {
            logger.LogWarning("Homebrew executable not found, skipping Homebrew packages");
            yield break;
        }

        // Run all three commands concurrently — descriptions are a bonus and failures are non-fatal.
        var infoTask = runner.RunAsync(_brewExecutablePath!, "info --json=v2 --installed", cancellationToken);

        var infoResult = await infoTask.ConfigureAwait(false);

        var packages = ParseBrewInfo(infoResult);
        if (packages is null)
        {
            logger.LogWarning("Failed to parse Homebrew info output, skipping Homebrew packages");
            yield break;
        }

        foreach (var formula in packages.Formulae)
        {
            var discoveredApp = new DiscoveredApp(this, formula.FullName ?? formula.Name, new AppIdentifier(Name, "Library", "Formula"), AppKind.Package)
            {
                BundleId = formula.Name,
                InstalledVersion = formula.InstalledVersion[0].Version,
                LatestVersion = formula.LatestVersion.StableVersion,
                Attribute = AppAttribute.Library | AppAttribute.HomebrewFormula,
                Description = formula.Description,
                OsvEcosystem = OsvEcosystemName.None
            };
            yield return discoveredApp;
        }

        foreach (var cask in packages.Casks)
        {
            if (_scannedApps.TryGetValue(cask.Name[0], out var app))
            {
                app.Description ??= cask.Description;

                if (cask.InstalledVersion == app.InstalledVersion &&
                    cask.Artifacts?.FirstOrDefault(c => c.App?.Length > 0)?.Target == app.Path &&
                    !app.Attribute.HasFlag(AppAttribute.AppStoreApp))
                {
                    // We've already scanned this app. Now, we've made sure that this is a Cask
                    app.BundleId ??= cask.Token;
                    app.Attribute |= AppAttribute.HomebrewCask;
                    app.LatestVersion = cask.LatestVersion;
                    app.Description ??= cask.Description;
                    app.Path ??= cask.Artifacts?.FirstOrDefault(c => c.App?.Length > 0)?.Target;
                }
                else
                {
                    // When the pointer reaches this line, means we haven't found this application during our scan
                    app.SubApps ??= [];
                    var brewSubApp = new DiscoveredApp(this, cask.Name[0], new AppIdentifier(Name, DisplayName, "Cask"), AppKind.App)
                    {
                        BundleId = cask.Token,
                        Attribute = AppAttribute.App | AppAttribute.MacApp | AppAttribute.HomebrewCask,
                        InstalledVersion = cask.InstalledVersion,
                        LatestVersion = cask.LatestVersion,
                        Description = cask.Description,
                        Path = cask.Artifacts?.FirstOrDefault(c => c.App?.Length > 0)?.Target,
                    };
                    app.SubApps.Add(brewSubApp);
                }

                continue;
            }

            // When the pointer reaches this line, means we haven't found this application during our scan
            var discoveredApp = new DiscoveredApp(this, cask.Name[0], new AppIdentifier(Name, DisplayName, "Cask"), AppKind.App)
            {
                BundleId = cask.Token,
                Attribute = AppAttribute.App | AppAttribute.MacApp | AppAttribute.HomebrewCask,
                InstalledVersion = cask.InstalledVersion,
                LatestVersion = cask.LatestVersion,
                Description = cask.Description,
                Path = cask.Artifacts?.FirstOrDefault(c => c.App?.Length > 0)
                    ?.Target,
            };
            yield return discoveredApp;
        }
    }

    /// <summary>
    /// Discovers pending macOS software updates via <c>softwareupdate --list --all</c>.
    /// Each item is emitted with <see cref="DiscoveredApp.LatestVersion"/> pre-filled so that
    /// <c>CheckAppAsync</c> treats it as already resolved (no further remote check needed).
    /// </summary>
    private async IAsyncEnumerable<DiscoveredApp> EnumerateSoftwareUpdates([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const string swuPath = "/usr/sbin/softwareupdate";
        if (!File.Exists(swuPath))
        {
            yield break;
        }

        var result = await runner.RunAsync(swuPath, "--list --all", cancellationToken);
        var output = result.StandardOutput + result.StandardError;

        string? currentLabel = null;
        string? currentVersion = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("* Label:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("** Label:", StringComparison.OrdinalIgnoreCase))
            {
                if (currentLabel is not null)
                {
                    yield return MakeSoftwareUpdateEntry(currentLabel, currentVersion);
                }

                var labelIdx = line.IndexOf("Label:", StringComparison.OrdinalIgnoreCase);
                currentLabel = line[(labelIdx + "Label:".Length)..].Trim();
                currentVersion = null;
                continue;
            }

            if (currentLabel is not null && line.Contains("Version:", StringComparison.OrdinalIgnoreCase))
            {
                currentVersion = ExtractVersionFromSoftwareUpdateLine(line);
            }
        }

        if (currentLabel is not null)
        {
            yield return MakeSoftwareUpdateEntry(currentLabel, currentVersion);
        }
    }

    private DiscoveredApp MakeSoftwareUpdateEntry(string label, string? version)
    {
        return new DiscoveredApp(this,
            label,
            new AppIdentifier(Name, "Software Update"),
            AppKind.App)
        {
            LatestVersion = version,
            UpdateInfo = version,
            Attribute = AppAttribute.None,
        };
    }

    private static string? ExtractVersionFromSoftwareUpdateLine(string line)
    {
        const string marker = "Version:";
        var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var after = line[(idx + marker.Length)..].TrimStart();
        var commaIdx = after.IndexOf(',');
        return commaIdx >= 0 ? after[..commaIdx].Trim() : after.Trim();
    }

    private async Task<DiscoveredApp> GetElectronApp(string bundlePath, string name, string version, string bundleId, List<DiscoveredApp> subApps, PlistInfo plist, CancellationToken cancellationToken)
    {
        string? methodDetail = null;
        var ymlPath = Path.Combine(bundlePath, "Contents", "Resources", "app-update.yml");
        if (File.Exists(ymlPath))
        {
            var yaml = await YamlReader.ReadAsync(ymlPath, cancellationToken).ConfigureAwait(false);
            if (yaml is not null)
            {
                var provider = yaml.GetString("provider");
                switch (provider)
                {
                    case "github":
                    {
                        var owner = yaml.GetString("owner");
                        var repo = yaml.GetString("repo");
                        if (!string.IsNullOrWhiteSpace(repo) && !string.IsNullOrWhiteSpace(owner))
                        {
                            methodDetail = $"github:{owner}/{repo}";
                        }

                        break;
                    }
                    case "generic":
                    {
                        var url = yaml.GetString("url");
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            methodDetail = $"generic:{url}";
                        }

                        break;
                    }
                    default:
                    {
                        logger.LogWarning("Electron app {App} has unrecognized update provider {Provider} in app-update.yml", name, provider);
                        break;
                    }
                }

                logger.LogDebug(
                    "ElectronScanner: {Name} v{Version} [{Provider}] at {Path}",
                    name, version, provider, bundlePath);
            }
        }

        var appIdentifier = new AppIdentifier(Name, DisplayName, "Electron");
        return new DiscoveredApp(this, name, appIdentifier, AppKind.App)
        {
            InstalledVersion = version,
            InstalledBuildNumber = null,
            BundleId = bundleId,
            Path = bundlePath,
            UpdateInfo = methodDetail,
            SubApps = subApps,
            Attribute = plist.Attribute
        };
    }

    private async Task<bool> GetLatestVersionBySparkleAsync(AppRecord record, CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("sparkle");
            using var response = await client.GetAsync(record.App.UpdateInfo, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var doc = new XmlDocument();
            doc.LoadXml(content);

            var nsMgr = new XmlNamespaceManager(doc.NameTable);
            nsMgr.AddNamespace("sparkle", "http://www.andymatuschak.org/xml-namespaces/sparkle");

            var itemNode = doc.SelectSingleNode("//rss/channel/item");
            var enclosureNode = itemNode?.SelectSingleNode("enclosure");
            if (enclosureNode is null)
            {
                return false;
            }

            var latestVersion = enclosureNode.Attributes?["sparkle:shortVersionString"]?.Value ??
                                itemNode?.SelectSingleNode("sparkle:shortVersionString", nsMgr)?.InnerText;
            var latestBuildNumber = enclosureNode.Attributes?["sparkle:version"]?.Value ??
                                    itemNode?.SelectSingleNode("sparkle:version", nsMgr)?.InnerText;
            if (latestVersion is null)
            {
                logger.LogDebug("Sparkle feed {FeedUrl} for {App} has no version", record.App.UpdateInfo, record.App.Name);
                return false;
            }

            record.App.Identifier = record.App.Identifier with { DisplayName = "Sparkle", Qualifier = "Application" };
            record.App.LatestVersion = latestVersion;
            record.App.LatestBuildNumber = latestBuildNumber;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Failed to fetch Sparkle feed {FeedUrl} for {App}: {Message}", record.App.UpdateInfo, record.App.Name, ex.Message);
            throw;
        }
    }

    private async Task<bool> GetLatestVersionByElectronAsync(AppRecord record, CancellationToken cancellationToken)
    {
        if (record.App.UpdateInfo is null)
        {
            return false;
        }

        string? latestVersion = null;
        if (record.App.UpdateInfo.StartsWith("generic:", StringComparison.OrdinalIgnoreCase))
        {
            var url = record.App.UpdateInfo["generic:".Length..];
            try
            {
                using var client = httpClientFactory.CreateClient("generic");
                client.BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/");
                using var response = await client.GetAsync("latest-mac.yml", cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var yaml = YamlReader.Parse(content);
                latestVersion = yaml.GetString("version");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Failed to fetch generic update URL {Url} for {App}: {Message}", url, record.App.Name, ex.Message);
                throw;
            }
        }
        else if (record.App.UpdateInfo.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = record.App.UpdateInfo.Split(':', 2);
            if (parts.Length != 2)
            {
                return false;
            }

            var repoParts = parts[1].Split('/', 2);
            if (repoParts.Length != 2)
            {
                return false;
            }

            var owner = repoParts[0];
            var repo = repoParts[1];

            try
            {
                using var client = httpClientFactory.CreateClient("github");
                using var response = await client.GetAsync($"/{owner}/{repo}/releases/latest/download/latest-mac.yml", cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var yaml = YamlReader.Parse(content);
                latestVersion = yaml.GetString("version");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    "Failed to fetch latest GitHub release for {App} from {Repo}: {Message}",
                    record.App.Name, parts[1], ex.Message);
                throw;
            }
        }

        if (latestVersion is null)
        {
            return false;
        }

        record.App.LatestVersion = latestVersion;
        return true;
    }

    /// <summary>
    /// Queries the iTunes Store Lookup API for the current App Store version.
    /// Prefers lookup by Apple ID; falls back to bundle ID.
    /// </summary>
    private async Task<bool> GetLatestVersionByITunesAsync(AppRecord record, CancellationToken cancellationToken)
    {
        string? query = null;

        if (record.App.UpdateInfo is { Length: > 0 } appleId && long.TryParse(appleId, out _))
        {
            query = $"/lookup?id={appleId}";
        }
        else if (record.App.BundleId is { Length: > 0 } bundleId)
        {
            query = $"/lookup?bundleId={bundleId}";
        }

        if (query is null)
        {
            return false;
        }

        try
        {
            using var client = httpClientFactory.CreateClient("itunes");
            using var response = await client.GetAsync(query, cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var result = await JsonSerializer.DeserializeAsync(stream, MacOsApplicationsJsonContext.Default.ItunesLookupResponse, cancellationToken).ConfigureAwait(false);
            if (result is null || result.ResultCount == 0 || result.Results is null)
            {
                logger.LogDebug(
                    "iTunes lookup for {App}: no results found (query: {Query})",
                    record.App.Name, query);
                return false;
            }

            var results = result.Results;
            var resp = results.FirstOrDefault(r => "mac-software".Equals(r.Kind, StringComparison.OrdinalIgnoreCase))
                       ?? results.FirstOrDefault(r => "software".Equals(r.Kind, StringComparison.OrdinalIgnoreCase));
            if (resp?.Version is null)
            {
                logger.LogDebug(
                    "iTunes lookup for {App}: no mac-software result found (response may contain iOS-only records)",
                    record.App.Name);
                return false;
            }

            if (record.App.Description is null && resp.Description is not null)
            {
                record.App.Description = resp.Description;
            }

            if ("mac-software".Equals(resp.Kind, StringComparison.OrdinalIgnoreCase))
            {
                record.App.LatestVersion = resp.Version;
                return true;
            }

            // Only an iOS / iPad App Store record came back: the app is an iOS app made available on
            // Apple Silicon Macs. The iTunes Search API reports just the iOS version, which Apple may
            // gate from Mac (the Mac App Store can install an older build), so resolve the real
            // Mac-installable version from the App Store product page rendered in Mac context.
            var macVersion = resp.TrackId > 0
                ? await GetMacInstallableVersionAsync(resp.TrackId, cancellationToken).ConfigureAwait(false)
                : null;

            if (macVersion is not null)
            {
                record.App.LatestVersion = macVersion;
                return true;
            }

            // Mac version could not be resolved — flag for manual review rather than misreporting
            // the app as outdated against an iOS version that cannot be installed on a Mac.
            logger.LogDebug(
                "iTunes lookup for {App}: only an iOS record (v{Version}) was returned and the Mac App Store version could not be resolved, flagging for manual review",
                record.App.Name,
                resp.Version);

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "iTunes lookup failed for {App}: {Message}",
                record.App.Name,
                ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Resolves the Mac-installable version of an iOS / iPad App Store app from its App Store
    /// product page rendered in Mac context (<c>?platform=mac</c>). The iTunes Search API only
    /// exposes the iOS version, which Apple may gate from Mac; the product page's most-recent
    /// version reflects what the Mac App Store will actually install. Returns <c>null</c> on any failure.
    /// </summary>
    private async Task<string?> GetMacInstallableVersionAsync(long trackId, CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("appstore-web");
            using var response = await client.GetAsync($"/us/app/id{trackId}?platform=mac", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ExtractMostRecentVersion(html);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to fetch Mac App Store page for track {TrackId}", trackId);
            return null;
        }
    }

    /// <summary>
    /// Extracts the most-recent version from the <c>serialized-server-data</c> JSON embedded in an
    /// App Store product page. Scans for the first <c>primarySubtitle</c> carrying a version number
    /// (the "What's New" entry). Returns <c>null</c> when the blob or a version cannot be found.
    /// </summary>
    private static string? ExtractMostRecentVersion(string html)
    {
        const string marker = "id=\"serialized-server-data\">";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        var json = Encoding.UTF8.GetBytes(html.Substring(start, end - start));
        var reader = new Utf8JsonReader(json);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("primarySubtitle"u8))
            {
                continue;
            }

            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
            {
                continue;
            }

            var match = VersionNumberRegex().Match(reader.GetString() ?? string.Empty);
            if (match.Success)
            {
                return match.Value;
            }
        }

        return null;
    }

    [GeneratedRegex(@"\d+(?:\.\d+)+", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex VersionNumberRegex();

    /// <summary>
    /// Queries <c>https://formulae.brew.sh/api/cask/{token}.json</c> for the latest version.
    /// Returns <c>null</c> on any failure (network, 404, parse error).
    /// </summary>
    private async Task<(string LatestVersion, string? Description)?> GetLatestVersionByCaskAsync(string token, string? appPath, CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("homebrew-api");
            using var response = await client.GetAsync($"/api/cask/{token}.json", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var result = await JsonSerializer.DeserializeAsync(stream, MacOsApplicationsJsonContext.Default.BrewCaskRecord, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                logger.LogDebug("Failed to parse Homebrew API response for '{Token}'", token);
                return null;
            }

            if (result.Artifacts is not { Length: > 0 })
            {
                return null;
            }

            var appArtifact = result.Artifacts.FirstOrDefault(c => c.App?.Length > 0)?.App?.Any(a => a.Equals(appPath, StringComparison.OrdinalIgnoreCase)) == true;
            var target = result.Artifacts.FirstOrDefault(c => c.Target?.Length > 0)?.Target?.Equals(appPath, StringComparison.OrdinalIgnoreCase) == true;
            var uninstallArtifact = result.Artifacts.FirstOrDefault(c => c.Uninstall?.Length > 0)?.Uninstall?.Any(u => u.Values.Any(paths => paths.Any(p => p.Equals(appPath, StringComparison.OrdinalIgnoreCase)))) == true;
            if (appArtifact || target || uninstallArtifact)
            {
                return (result.LatestVersion, result.Description);
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to fetch cask version from Homebrew API for '{Token}'", token);
            return null;
        }
    }

    /// <summary>
    /// Parses the <c>brew info --json=v2 --installed</c> output into description and display-name maps.
    /// Failures are silently ignored — descriptions are non-critical.
    /// </summary>
    private BrewInfoRoot? ParseBrewInfo(ProcessResult result)
    {
        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        try
        {
            var info = JsonSerializer.Deserialize(result.StandardOutput, MacOsApplicationsJsonContext.Default.BrewInfoRoot);
            return info;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to parse 'brew info --json=v2 --installed' output");
            return null;
        }
    }

    private async Task<PlistInfo?> GetPlistInfo(string bundlePath, CancellationToken cancellationToken)
    {
        PlistInfo? plist = null;
        try
        {
            plist = await plistReader.ReadAsync(bundlePath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to read plist for {Bundle}", bundlePath);
        }

        return plist;
    }

    private static string? Normalize(string? value)
    {
        return value?.Trim().Normalize(NormalizationForm.FormC).Replace("\u200E", string.Empty);
    }

    private static string CreateToken(string appName)
    {
        // "Visual Studio Code" → "visual-studio-code", "1Password" → "1password"
        return string.Create(appName.Length, appName, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                span[i] = c switch
                {
                    ' ' or '_' => '-',
                    _ => char.ToLowerInvariant(c)
                };
            }
        }).TrimEnd('-');
    }
}