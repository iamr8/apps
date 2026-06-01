using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Xml;

using apps.Infrastructure;
using apps.Models;

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
public sealed class MacApplicationsScanner(
    PlistReader plistReader,
    IProcessRunner runner,
    IHttpClientFactory httpClientFactory,
    ILogger<MacApplicationsScanner> logger)
    : IScanner
{
    private Dictionary<string, bool> _appsExecutablePaths = [];
    private string? _brewExecutablePath;

    public int Order => 0; // Relatively fast, and many apps are prerequisites for other scanners (e.g. browsers hosting PWAs or extensions).

    public string Name => "Applications";

    /// <inheritdoc/>
    public string DisplayName => "Applications";

    public OS SupportedOS => OS.MacOS;
    public AppKind Kind => AppKind.SystemApp | AppKind.App | AppKind.Extension | AppKind.Package;

    private readonly ConcurrentDictionary<string, PlistInfo> _plistCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DiscoveredApp> _appCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, (BrewCaskRecord Cask, DiscoveredApp App)> _casks = [];
    private readonly ConcurrentDictionary<string, (BrewFormulaRecord Formula, DiscoveredApp App)> _formulas = [];

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
            _appCache[app.Name] = app;
            yield return app;
        }

        await foreach (var app in EnumerateHomebrew(cancellationToken))
        {
            if (_appCache.TryGetValue(app.Name, out var cachedApp))
            {
                // We check if the Homebrew app is the same as an app discovered in the Applications folder.
                if (cachedApp.Path.Equals(app.Path, StringComparison.OrdinalIgnoreCase))
                {
                    // We check if the installed versions are the same.
                    // If they are, we merge the Homebrew description into the existing record.
                    var normalizedBrewVersion = app.InstalledVersion.Split(',')[0];
                    if (normalizedBrewVersion.Equals(cachedApp.InstalledVersion))
                    {
                        if (cachedApp.Description is null && app.Description is not null)
                        {
                            cachedApp.Description = app.Description; // enrich existing record with Homebrew description if missing
                        }

                        if (cachedApp.UpdateMethod is null && app.UpdateMethod is not null)
                        {
                            cachedApp.UpdateMethod = app.UpdateMethod; // enrich existing record with Homebrew suggested method if missing
                            cachedApp.UpdateMethodDetail = app.UpdateMethodDetail;
                        }
                    }
                    else
                    {
                        // Realized that they're different, so we add the Homebrew app as a sub-app.
                        cachedApp.SubApps ??= [];
                        app.Description = null;
                        cachedApp.SubApps.Add(app);
                    }
                }
                else
                {
                    logger.LogWarning("Homebrew app {AppName} has the same name as an app discovered in the Applications folder but different path, skipping Homebrew record to avoid conflicts", app.Name);
                }

                continue;
            }

            _appCache[app.Name] = app;
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

        var unresolvedApps = apps.Except(resolvedApps).ToList();
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
            var tuple = await GetLatestVersionByCaskAsync(token, cancellationToken).ConfigureAwait(false);
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
            record.App.UpdateMethod = UpdateMethod.HomebrewCask;
            await writer.WriteAsync((record, true, false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await writer.WriteAsync((record, false, true), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CheckAppAsync(AppRecord record, ChannelWriter<(AppRecord App, bool Resolved, bool Error)> writer, CancellationToken cancellationToken)
    {
        if (record.App.LatestVersion is not null)
        {
            logger.LogDebug("App {AppName} is already updated to v{Version}", record.App.Name, record.App.InstalledVersion);
            await writer.WriteAsync((record, true, false), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (record.App is { UpdateMethod: UpdateMethod.HomebrewCask or UpdateMethod.HomebrewFormula })
        {
            // Will be published on its method-specific check.
            logger.LogDebug("App {AppName} has suggested Homebrew update method, skipping iTunes lookup", record.App.Name);
            return;
        }

        try
        {
            if (record.App.SparkleFeedUrl is not null)
            {
                if (await GetLatestVersionBySparkleAsync(record, cancellationToken))
                {
                    await writer.WriteAsync((record, true, false), cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            else if (record.App is { UpdateMethod: UpdateMethod.Electron, UpdateMethodDetail: not null })
            {
                if (await GetLatestVersionByElectronAsync(record, cancellationToken))
                {
                    await writer.WriteAsync((record, true, false), cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            else if (await GetLatestVersionByITunesAsync(record, cancellationToken))
            {
                await writer.WriteAsync((record, true, false), cancellationToken).ConfigureAwait(false);
                return;
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

                // System app: physically lives under /System/Applications OR has a com.apple.* bundle ID.
                var isSystemApp = rootIsSystem || IsAppleBundleId(bundleId);
                if (isSystemApp)
                {
                    logger.LogDebug("Identified system app: {Name} [{BundleId}] at {Path}", name, bundleId ?? "—", bundlePath);
                    continue;
                }

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
                                if (appexPlist.IsSafariExtension)
                                {
                                    plist = plist with { IsSafariExtension = true }; // propagate to parent app for easier detection
                                    break;
                                }
                                //
                                // var appexName = Normalize(appexPlist.DisplayName ?? Path.GetFileNameWithoutExtension(appexPath));
                                // if (string.IsNullOrWhiteSpace(appexName))
                                // {
                                //     continue;
                                // }
                                //
                                // var appexIndentifier = new AppIdentifier("OS", "OS", "Extension");
                                // subApps.Add(new DiscoveredApp(appexName, appexIndentifier, AppKind.Extension)
                                // {
                                //     InstalledVersion = version,
                                //     InstalledBuildVersion = buildVersion,
                                //     BundleId = bundleId,
                                //     Path = appexPath,
                                // });
                            }
                        }
                    }
                }

                if (plist.IsElectronApp)
                {
                    var electronApp = await GetElectronApp(bundlePath, name, version, bundleId, subApps, cancellationToken);
                    yield return electronApp with { SubApps = subApps };
                    continue;
                }

                UpdateMethod? suggestedMethod = null;
                string? suggestedDetail = null;
                string? suFeedUrl = null;

                if (IsWebApp(bundleId))
                {
                    // PWA / browser-hosted web app: the browser manages updates, no external check needed
                    suggestedMethod = null;
                }
                else if (!string.IsNullOrWhiteSpace(plist?.SparkleUrl))
                {
                    suggestedMethod = UpdateMethod.Sparkle;
                    suggestedDetail = plist.SparkleUrl;
                    suFeedUrl = plist.SparkleUrl;
                }
                else if (IsMasInstalled(bundlePath))
                {
                    // AppStore (priority 1) beats Sparkle (priority 4): prefer App Store even when
                    // the bundle also advertises a Sparkle feed.
                    suggestedMethod = UpdateMethod.AppStore;
                }

                logger.LogDebug(
                    "Discovered {Kind} {Name} v{Version} [{BundleId}] at {Path}",
                    AppKind.App, name, version, bundleId ?? "—", bundlePath);

                var appIdentifier = plist.IsSafariExtension
                    ? new AppIdentifier("SafariExt", "Safari", "Extension")
                    : new AppIdentifier(Name, "Application");
                yield return new DiscoveredApp(this, name, appIdentifier, plist.IsSafariExtension ? AppKind.Extension : AppKind.App)
                {
                    InstalledVersion = version,
                    InstalledBuildNumber = buildVersion,
                    BundleId = bundleId,
                    Path = bundlePath,
                    UpdateMethod = suggestedMethod,
                    UpdateMethodDetail = suggestedDetail,
                    SparkleFeedUrl = suFeedUrl,
                    SubApps = subApps
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

        foreach (var formula in packages.Formulae)
        {
            var discoveredApp = new DiscoveredApp(this, formula.FullName ?? formula.Name, new AppIdentifier(Name, "Library", "Formula"), AppKind.Package)
            {
                BundleId = formula.Name,
                InstalledVersion = formula.InstalledVersion[0].Version,
                LatestVersion = formula.LatestVersion.StableVersion,
                UpdateMethod = UpdateMethod.HomebrewFormula,
                Description = formula.Description,
                OsvEcosystem = OsvEcosystemName.None
            };
            _formulas[formula.Name] = (formula, discoveredApp);
            yield return discoveredApp;
        }

        foreach (var cask in packages.Casks)
        {
            var discoveredApp = new DiscoveredApp(this, cask.Name[0], new AppIdentifier(Name, DisplayName, "Cask"), AppKind.App)
            {
                BundleId = cask.Token,
                InstalledVersion = cask.InstalledVersion,
                LatestVersion = cask.LatestVersion,
                UpdateMethod = UpdateMethod.HomebrewCask,
                Description = cask.Description,
                Path = cask.Artifacts?.FirstOrDefault(c => c.App?.Length > 0)?.Target
            };
            _casks[cask.Token] = (cask, discoveredApp);
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
            UpdateMethod = UpdateMethod.Specialised,
            UpdateMethodDetail = version,
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

    private async Task<DiscoveredApp> GetElectronApp(string bundlePath, string name, string version, string bundleId, List<DiscoveredApp> subApps, CancellationToken cancellationToken)
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

        var appIdentifier = new AppIdentifier(Name, "Electron", "Application");
        return new DiscoveredApp(this, name, appIdentifier, AppKind.App)
        {
            InstalledVersion = version,
            InstalledBuildNumber = null,
            BundleId = bundleId,
            Path = bundlePath,
            UpdateMethod = UpdateMethod.Electron,
            UpdateMethodDetail = methodDetail,
            SubApps = subApps
        };
    }

    private async Task<bool> GetLatestVersionBySparkleAsync(AppRecord record, CancellationToken cancellationToken)
    {
        if (record.App.SparkleFeedUrl is null)
        {
            return false;
        }

        try
        {
            using var client = httpClientFactory.CreateClient("sparkle");
            using var response = await client.GetAsync(record.App.SparkleFeedUrl, cancellationToken).ConfigureAwait(false);
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
                logger.LogDebug("Sparkle feed {FeedUrl} for {App} has no version", record.App.SparkleFeedUrl, record.App.Name);
                return false;
            }

            record.App.Identifier = record.App.Identifier with { DisplayName = "Sparkle", Qualifier = "Application" };
            record.App.LatestVersion = latestVersion;
            record.App.LatestBuildNumber = latestBuildNumber;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "Failed to fetch Sparkle feed {FeedUrl} for {App}: {Message}",
                record.App.SparkleFeedUrl, record.App.Name, ex.Message);
            throw;
        }
    }

    private async Task<bool> GetLatestVersionByElectronAsync(AppRecord record, CancellationToken cancellationToken)
    {
        if (record.App.UpdateMethodDetail is null)
        {
            return false;
        }

        string? latestVersion = null;
        if (record.App.UpdateMethodDetail.StartsWith("generic:", StringComparison.OrdinalIgnoreCase))
        {
            var url = record.App.UpdateMethodDetail["generic:".Length..];
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
        else if (record.App.UpdateMethodDetail.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = record.App.UpdateMethodDetail.Split(':', 2);
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

        if (record.App.UpdateMethodDetail is { Length: > 0 } appleId && long.TryParse(appleId, out _))
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

            // this: we have a False Positive here. This API realizes iOS apps as macOS softwares.
            // We need to check iOS (WrappedBundle)
            // We need a two-level fetch process. `desktopSoftware` delivers metadata for mac-native software.
            // `macSoftware` seems to be more broad, also includes Catalyst and iOS-only software.
            // The former however is more accurate, as `macSoftware` might return iPad metadata for certain apps.
            // We therefore prefer `desktopSoftware` and fall back to `macSoftware` if no info was found.
            var resp = result?.Results?.FirstOrDefault(r => r.Kind.Equals("desktop-software", StringComparison.OrdinalIgnoreCase) ||
                                                            r.Kind.Equals("mac-software", StringComparison.OrdinalIgnoreCase) ||
                                                            r.Kind.Equals("software", StringComparison.OrdinalIgnoreCase));
            if (resp?.Version is null)
            {
                logger.LogDebug(
                    "iTunes lookup for {App}: no mac-software result found (response may contain iOS-only records)",
                    record.App.Name);
                return false;
            }

            if (resp.SupportedDevices is not null)
            {
                if (!resp.SupportedDevices.Any(c => c.Contains("Mac", StringComparison.OrdinalIgnoreCase)))
                {
                    // FALSE Positive risk!
                    logger.LogDebug(
                        "iTunes lookup for {App}: no Mac supported device found in response, skipping (response may contain iOS-only records)",
                        record.App.Name);
                    return false;
                }
            }

            if (record.App.Identifier.DisplayName == "Application")
            {
                record.App.Identifier = record.App.Identifier with { DisplayName = "App Store", Qualifier = "Application" };
            }

            if (record.App.Description is null && resp.Description is not null)
            {
                record.App.Description = resp.Description;
            }

            record.App.UpdateMethod = UpdateMethod.AppStore;
            record.App.LatestVersion = resp.Version;
            return true;
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
    /// Queries <c>https://formulae.brew.sh/api/cask/{token}.json</c> for the latest version.
    /// Returns <c>null</c> on any failure (network, 404, parse error).
    /// </summary>
    private async Task<(string LatestVersion, string? Description)?> GetLatestVersionByCaskAsync(string token, CancellationToken cancellationToken)
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

            return (result.LatestVersion, result.Description);
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
        if (_plistCache.TryGetValue(bundlePath, out var cachedPlist))
        {
            return cachedPlist;
        }

        PlistInfo? plist = null;
        try
        {
            plist = await plistReader.ReadAsync(bundlePath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to read plist for {Bundle}", bundlePath);
        }

        if (plist is null)
        {
            return null;
        }

        _plistCache[bundlePath] = plist;
        return plist;
    }

    private static bool IsAppleBundleId(string? bundleId)
    {
        return bundleId is not null && bundleId.StartsWith("com.apple.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWebApp(string? bundleId)
    {
        return bundleId is not null &&
               (bundleId.StartsWith("com.apple.Safari.WebApp.", StringComparison.OrdinalIgnoreCase) ||
                bundleId.StartsWith("com.google.Chrome.app.", StringComparison.OrdinalIgnoreCase) ||
                bundleId.StartsWith("com.microsoft.edgeapp.", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns <see langword="true"/> when the bundle is an App Store install.
    /// Detected by either the <c>Contents/_MASReceipt/</c> directory (native macOS apps)
    /// or the <c>Wrapper/</c> directory (iOS/iPadOS apps running on Apple Silicon).
    /// </summary>
    private static bool IsMasInstalled(string bundlePath)
    {
        return Directory.Exists(Path.Combine(bundlePath, "Contents", "_MASReceipt"))
               || Directory.Exists(Path.Combine(bundlePath, "Wrapper"));
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