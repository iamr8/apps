using System.Collections.Concurrent;
using System.Globalization;
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

    private static readonly TimeSpan DefaultBrewCacheMaxAge = TimeSpan.FromHours(6);

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
        var resolvedApps = new HashSet<AppRecord>();
        var resolvedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var erroredApps = new HashSet<AppRecord>();
        await foreach (var (app, resolved, error) in apps.WhenAll<AppRecord, (AppRecord App, bool Resolved, bool Error)>(onPublication: CheckAppAsync, cancellationToken: cancellationToken))
        {
            if (error)
            {
                erroredApps.Add(app);
            }

            if (!resolved)
            {
                continue;
            }

            resolvedApps.Add(app);
            resolvedNames.Add(app.App.Name);
            yield return (app, error);
        }

        var unresolvedApps = apps.Where(a => !resolvedNames.Contains(a.App.Name)).ToList();
        await foreach (var (app, resolved, error) in unresolvedApps.WhenAll<AppRecord, (AppRecord App, bool Resolved, bool Error)>(onPublication: CheckHomebrewAsync, cancellationToken: cancellationToken))
        {
            if (error)
            {
                erroredApps.Add(app);
            }

            if (!resolved)
            {
                continue;
            }

            erroredApps.Remove(app); // a later pass resolved it — no longer counts as a failed check
            resolvedApps.Add(app);
            yield return (app, error);
        }

        var stillUnresolved = apps.Where(a => !resolvedApps.Contains(a)).ToList();
        foreach (var record in stillUnresolved)
        {
            var errored = erroredApps.Contains(record);
            logger.LogDebug("Failed to resolve update information for {AppName}, skipping (errored: {Errored})", record.App.Name, errored);
            yield return (record, errored);
        }
    }

    private async Task CheckHomebrewAsync(AppRecord record, ChannelWriter<(AppRecord App, bool Resolved, bool Error)> writer, CancellationToken cancellationToken)
    {
        try
        {
            if (record.App.LatestVersion is not null)
            {
                logger.LogDebug("App {AppName} already has its latest version (v{LatestVersion}) resolved during discovery, skipping remote check", record.App.Name, record.App.LatestVersion);
                await writer.WriteAsync((record, true, false), cancellationToken).ConfigureAwait(false);
                return;
            }

            var tuple = await GetLatestVersionByCaskAsync(record, cancellationToken).ConfigureAwait(false);
            if (tuple is null)
            {
                logger.LogDebug("No Homebrew information found for {AppName}", record.App.Name);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
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
                else
                {
                    // When an app has the App Store attribute, but we fail to get update information from iTunes,
                    // it may be due to a transient lookup failure or macOS System App (e.g., Safari).
                    logger.LogDebug("App {AppName} has the App Store attribute but failed to get update information from iTunes, skipping", record.App.Name);
                    await writer.WriteAsync((record, true, false), cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            else if (record.App.Attribute.HasFlag(AppAttribute.PwaApp))
            {
                // PWAs are typically distributed outside the App Store and may not have a Sparkle feed, so we'll skip them.
                logger.LogDebug("App {AppName} is identified as a PWA, which may not have a standard update mechanism", record.App.Name);
                await writer.WriteAsync((record, true, false), cancellationToken).ConfigureAwait(false);
                return;
            }
            else
            {
                // This app seems to be installed manually by the user, so we'll skip it.
                logger.LogDebug("App {AppName} has no identifiable update method (not Sparkle, Electron, or App Store), skipping", record.App.Name);
            }

            logger.LogDebug("No update information found for {AppName}", record.App.Name);
            await writer.WriteAsync((record, false, false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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

                logger.LogDebug(
                    "Discovered {Kind} {Name} v{Version} [{BundleId}] at {Path}",
                    AppKind.App, name, version, bundleId ?? "—", bundlePath);

                string? updateInfo = null;
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
                else if (plist.Attribute.HasFlag(AppAttribute.SparkleFeed))
                {
                    appIdentifier = new AppIdentifier(Name, DisplayName, "Sparkle");
                    updateInfo = plist.SparkleUrl;
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

        await RefreshBrewApiCacheIfStaleAsync(
                _brewExecutablePath!,
                ResolveBrewApiCacheDir(),
                DateTimeOffset.UtcNow,
                ResolveBrewCacheMaxAge(),
                cancellationToken)
            .ConfigureAwait(false);

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
                ApplyCaskToScannedApp(app, cask);
                continue;
            }

            // The cask's display name didn't match any scanned bundle, but the same app may have
            // been installed manually under a different name. Match on hard evidence (artifact
            // path / bundle id) so the manual bundle and the cask collapse into one entry instead
            // of being reported twice.
            var manual = _scannedApps.Values.FirstOrDefault(scanned =>
                !scanned.Attribute.HasFlag(AppAttribute.AppStoreApp) && CaskArtifactMatchesApp(cask, scanned));
            if (manual is not null)
            {
                ApplyCaskToScannedApp(manual, cask);
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
    /// Attaches a Homebrew cask to an app already found during the filesystem scan as a sub-app
    /// (one level deep). The scanned bundle and its cask are two update channels for the same app
    /// — a Sparkle self-updater versus the cask — whose versions can legitimately differ, so both
    /// surface: the scanned app stays the parent and the cask becomes its sub-app. When the cask
    /// installs this very bundle (<see cref="CaskInstallsApp"/>), the on-disk version is
    /// authoritative — Homebrew's receipt lags for casks that auto-update themselves — so the
    /// sub-app reports the parent's installed version instead of the stale receipt, avoiding a
    /// false "outdated". A cask that manages a different bundle keeps its own recorded version.
    /// </summary>
    internal void ApplyCaskToScannedApp(DiscoveredApp app, BrewCaskRecord cask)
    {
        app.Description ??= cask.Description;
        app.BundleId ??= cask.Token;

        var installedVersion = CaskInstallsApp(cask, app) ? app.InstalledVersion : cask.InstalledVersion;

        app.SubApps ??= [];
        app.SubApps.Add(new DiscoveredApp(this, cask.Name[0], new AppIdentifier(Name, DisplayName, "Cask"), AppKind.App)
        {
            BundleId = cask.Token,
            Attribute = AppAttribute.App | AppAttribute.MacApp | AppAttribute.HomebrewCask,
            InstalledVersion = installedVersion,
            LatestVersion = cask.LatestVersion,
            Description = cask.Description,
            Path = cask.Artifacts?.FirstOrDefault(c => c.App?.Length > 0)?.Target ?? app.Path,
        });
    }

    /// <summary>
    /// Refreshes Homebrew's local API cache via <c>brew update --quiet</c> when it is older than
    /// <paramref name="maxAge"/>. <c>brew info</c> — the only Homebrew command this scanner runs —
    /// never triggers Homebrew's own auto-update, so without this the cached <c>versions.stable</c>
    /// can lag the real latest and outdated formulae/casks are silently reported as up-to-date.
    /// A failed or slow refresh is non-fatal: a stale cache still yields a best-effort result.
    /// </summary>
    internal async Task RefreshBrewApiCacheIfStaleAsync(
        string brewExe,
        string apiCacheDir,
        DateTimeOffset nowUtc,
        TimeSpan maxAge,
        CancellationToken cancellationToken)
    {
        var newestWriteUtc = GetNewestApiCacheWriteUtc(apiCacheDir);
        if (!IsCacheStale(newestWriteUtc, nowUtc, maxAge))
        {
            logger.LogDebug("Homebrew API cache is fresh (written {Written}), skipping refresh", newestWriteUtc?.ToString("o", CultureInfo.InvariantCulture));
            return;
        }

        logger.LogDebug("Homebrew API cache is stale (written {Written}), running '{Exe} update' to refresh", newestWriteUtc?.ToString("o", CultureInfo.InvariantCulture) ?? "never", brewExe);

        var result = await runner.RunAsync(brewExe, "update --quiet", cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            logger.LogWarning("'{Exe} update' failed (exit {Code}): {Error}", brewExe, result.ExitCode, result.StandardError.Trim());
        }
    }

    /// <summary>
    /// Discovers pending macOS software updates via <c>softwareupdate --list --no-scan</c>.
    /// Each item is emitted with <see cref="DiscoveredApp.LatestVersion"/> pre-filled so that
    /// <c>CheckAppAsync</c> treats it as already resolved (no further remote check needed).
    /// </summary>
    /// <remarks>
    /// <c>--no-scan</c> reads the result of macOS's own periodic background scan instead of
    /// contacting Apple's servers synchronously. A live <c>--list</c> can block 10–50s on Apple's
    /// latency and gate the entire scan phase; the cached result is effectively instant and stays
    /// fresh because macOS scans on its own schedule.
    /// </remarks>
    private async IAsyncEnumerable<DiscoveredApp> EnumerateSoftwareUpdates([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const string swuPath = "/usr/sbin/softwareupdate";
        if (!File.Exists(swuPath))
        {
            yield break;
        }

        var result = await runner.RunAsync(swuPath, "--list --no-scan", cancellationToken);
        var output = result.StandardOutput + result.StandardError;

        foreach (var (label, version) in ParseSoftwareUpdates(output))
        {
            yield return MakeSoftwareUpdateEntry(label, version);
        }
    }

    /// <summary>
    /// Parses the textual output of <c>softwareupdate --list --all</c> into label/version pairs.
    /// Each pending update is delimited by a <c>* Label:</c> (or <c>** Label:</c>) line; the
    /// optional version is read from a following <c>Version:</c> line. Pure and deterministic.
    /// </summary>
    internal static List<(string Label, string? Version)> ParseSoftwareUpdates(string output)
    {
        var entries = new List<(string Label, string? Version)>();
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
                    entries.Add((currentLabel, currentVersion));
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
            entries.Add((currentLabel, currentVersion));
        }

        return entries;
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

    /// <summary>
    /// Reads the version token from a single <c>softwareupdate</c> output line containing a
    /// <c>Version:</c> marker, stopping at the first comma (e.g. <c>"Version: 13.5, Size: …"</c>
    /// yields <c>13.5</c>). Returns <see langword="null"/> when no marker is present.
    /// </summary>
    internal static string? ExtractVersionFromSoftwareUpdateLine(string line)
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
            return true; // fail as "up-to-date" to avoid false positives on lookup failure, which is often transient and non-critical
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
    internal static string? ExtractMostRecentVersion(string html)
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
    private async Task<(string LatestVersion, string? Description)?> GetLatestVersionByCaskAsync(AppRecord record, CancellationToken cancellationToken)
    {
        var token = CreateToken(record.App.Name);
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

            foreach (var artifact in result.Artifacts)
            {
                if (artifact.App?.Any(a => a.Equals(record.App.Path, StringComparison.OrdinalIgnoreCase)) == true ||
                    artifact.App?.Any(a => record.App.BundleId?.Equals(a, StringComparison.OrdinalIgnoreCase) == true) == true)
                {
                    return (result.LatestVersion, result.Description);
                }

                if (artifact.Target is { Length: > 0 } &&
                    artifact.Target.Equals(record.App.Path, StringComparison.OrdinalIgnoreCase) ||
                    record.App.BundleId?.Equals(artifact.Target, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return (result.LatestVersion, result.Description);
                }

                if (artifact.Uninstall is { Length: > 0 })
                {
                    foreach (var uninstall in artifact.Uninstall)
                    {
                        foreach (var (key, value) in uninstall)
                        {
                            if (value.ValueKind == JsonValueKind.Array)
                            {
                                if (value.EnumerateArray().Any(p =>
                                    {
                                        var str = p.GetString();
                                        if (str is null)
                                        {
                                            return false;
                                        }

                                        return str.Equals(record.App.Path, StringComparison.OrdinalIgnoreCase) ||
                                               record.App.BundleId?.Equals(str, StringComparison.OrdinalIgnoreCase) == true;
                                    }))
                                {
                                    return (result.LatestVersion, result.Description);
                                }
                            }
                            else if (value.ValueKind == JsonValueKind.String)
                            {
                                var str = value.GetString();
                                if (str?.Equals(record.App.Path, StringComparison.OrdinalIgnoreCase) == true ||
                                    record.App.BundleId?.Equals(str, StringComparison.OrdinalIgnoreCase) == true)
                                {
                                    return (result.LatestVersion, result.Description);
                                }
                            }
                        }
                    }
                }
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
        try
        {
            return ParseBrewInfo(result.StandardOutput, result.Success);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to parse 'brew info --json=v2 --installed' output");
            return null;
        }
    }

    /// <summary>
    /// Deserializes <c>brew info --json=v2 --installed</c> stdout into a <see cref="BrewInfoRoot"/>.
    /// Returns <see langword="null"/> when the command failed or produced no output. Throws
    /// <see cref="JsonException"/> on malformed JSON so callers can decide how to report it.
    /// </summary>
    internal static BrewInfoRoot? ParseBrewInfo(string? standardOutput, bool success)
    {
        if (!success || string.IsNullOrWhiteSpace(standardOutput))
        {
            return null;
        }

        return JsonSerializer.Deserialize(standardOutput, MacOsApplicationsJsonContext.Default.BrewInfoRoot);
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

    /// <summary>
    /// Trims, NFC-normalizes, and strips left-to-right marks from a plist-derived string so names
    /// and versions compare and render consistently. Returns <see langword="null"/> for null input.
    /// </summary>
    internal static string? Normalize(string? value)
    {
        return value?.Trim().Normalize(NormalizationForm.FormC).Replace("\u200E", string.Empty);
    }

    /// <summary>
    /// Returns the most recent write time among <c>*.jws.json</c> files under
    /// <paramref name="apiCacheDir"/> (Homebrew's downloaded API payloads), or <see langword="null"/>
    /// when the directory is absent or holds none. Globs recursively so it tracks the payload
    /// regardless of Homebrew's layout (<c>formula.jws.json</c> vs <c>internal/packages.*.jws.json</c>).
    /// </summary>
    internal static DateTimeOffset? GetNewestApiCacheWriteUtc(string apiCacheDir)
    {
        if (!Directory.Exists(apiCacheDir))
        {
            return null;
        }

        DateTimeOffset? newest = null;
        foreach (var file in Directory.EnumerateFiles(apiCacheDir, "*.jws.json", SearchOption.AllDirectories))
        {
            var write = File.GetLastWriteTimeUtc(file);
            if (newest is null || write > newest.Value.UtcDateTime)
            {
                newest = new DateTimeOffset(write, TimeSpan.Zero);
            }
        }

        return newest;
    }

    /// <summary>
    /// Decides whether a cask should merge into <paramref name="app"/> when the two were already
    /// paired by display name. Merges when the cask's artifacts give positive evidence of this
    /// bundle (see <see cref="CaskArtifactMatchesApp"/>), or when the cask carries no path evidence
    /// at all — in which case the display-name match stands. Reusable for any auto-updating cask:
    /// it never assumes an explicit <c>target</c> is present.
    /// </summary>
    internal static bool CaskInstallsApp(BrewCaskRecord cask, DiscoveredApp app)
    {
        return CaskArtifactMatchesApp(cask, app) || !CaskHasAppPathEvidence(cask);
    }

    /// <summary>
    /// Positive, name-independent evidence that <paramref name="cask"/> installs the same bundle as
    /// <paramref name="app"/>: an artifact <c>target</c> or <c>app</c> entry that equals the app's
    /// path, its bundle id, or its <c>*.app</c> file name (case-insensitive, trailing slash ignored).
    /// Use this for cross-source de-duplication where the display names differ and only hard
    /// evidence may collapse the two records.
    /// </summary>
    internal static bool CaskArtifactMatchesApp(BrewCaskRecord cask, DiscoveredApp app)
    {
        if (cask.Artifacts is not { Length: > 0 })
        {
            return false;
        }

        foreach (var artifact in cask.Artifacts)
        {
            if (artifact.Target is { Length: > 0 } target &&
                (PathEquals(target, app.Path) || string.Equals(target, app.BundleId, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (artifact.App is not { Length: > 0 })
            {
                continue;
            }

            foreach (var appEntry in artifact.App)
            {
                if (PathEquals(appEntry, app.Path) ||
                    string.Equals(appEntry, app.BundleId, StringComparison.OrdinalIgnoreCase) ||
                    (app.Path is { Length: > 0 } path && PathEquals(appEntry, Path.GetFileName(path.TrimEnd('/')))))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>True when any of the cask's artifacts names an install <c>target</c> or <c>app</c> bundle.</summary>
    private static bool CaskHasAppPathEvidence(BrewCaskRecord cask)
    {
        return cask.Artifacts is { Length: > 0 }
            && cask.Artifacts.Any(a => a.Target is { Length: > 0 } || a.App is { Length: > 0 });
    }

    /// <summary>Case-insensitive path comparison that ignores a single trailing slash on either side.</summary>
    private static bool PathEquals(string? a, string? b)
    {
        return a is not null && b is not null && string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A cache is stale when it has never been written or is older than <paramref name="maxAge"/>.</summary>
    internal static bool IsCacheStale(DateTimeOffset? newestWriteUtc, DateTimeOffset nowUtc, TimeSpan maxAge)
    {
        return newestWriteUtc is not { } written || nowUtc - written > maxAge;
    }

    /// <summary>
    /// Resolves Homebrew's API cache directory, honouring the <c>HOMEBREW_CACHE</c> override and
    /// falling back to the default <c>~/Library/Caches/Homebrew</c>.
    /// </summary>
    internal static string ResolveBrewApiCacheDir()
    {
        var cache = Environment.GetEnvironmentVariable("HOMEBREW_CACHE");
        if (string.IsNullOrWhiteSpace(cache))
        {
            cache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Caches", "Homebrew");
        }

        return Path.Combine(cache, "api");
    }

    /// <summary>
    /// Reads the cache freshness window from <c>APPS_BREW_CACHE_MAX_AGE_HOURS</c> (a non-negative
    /// number of hours), defaulting to <see cref="DefaultBrewCacheMaxAge"/> when unset or invalid.
    /// </summary>
    private static TimeSpan ResolveBrewCacheMaxAge()
    {
        var raw = Environment.GetEnvironmentVariable("APPS_BREW_CACHE_MAX_AGE_HOURS");
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) && hours >= 0
            ? TimeSpan.FromHours(hours)
            : DefaultBrewCacheMaxAge;
    }

    /// <summary>
    /// Derives a Homebrew cask token from an app's display name by lowercasing and replacing
    /// spaces/underscores with hyphens (e.g. <c>"Visual Studio Code"</c> \u2192 <c>"visual-studio-code"</c>).
    /// </summary>
    internal static string CreateToken(string appName)
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