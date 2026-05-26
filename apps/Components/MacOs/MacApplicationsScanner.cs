using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using apps.Infrastructure;
using apps.Scanners;
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
public sealed class MacApplicationsScanner(PlistReader plistReader, ILogger<MacApplicationsScanner> logger)
    : IScanner
{
    private Dictionary<string, bool> _executablePaths = [];

    public string Name => "Applications";

    /// <inheritdoc/>
    public string DisplayName => "Applications";

    public OS SupportedOS => OS.MacOS;

    /// <inheritdoc/>
    public bool IsAvailable()
    {
        var scanRoots = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            { "/Applications", false },
            { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"), false },
            // Chrome PWA apps are installed here by Chrome's "Add to Dock" / "Create shortcut" feature
            { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "Chrome Apps.localized"), false },
            { "/Applications/Utilities", false },
            { "/System/Applications", true }
        };
        _executablePaths = scanRoots.Keys.Where(Directory.Exists).ToDictionary(path => path, path => scanRoots[path]);
        return _executablePaths.Count > 0;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var (root, rootIsSystem) in _executablePaths)
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
                var app = await BuildDiscoveredAppAsync(bundlePath, rootIsSystem, cancellationToken);
                if (app is not null)
                {
                    yield return app;
                }
            }
        }
    }

    private async Task<DiscoveredApp?> BuildDiscoveredAppAsync(string bundlePath, bool rootIsSystem, CancellationToken cancellationToken)
    {
        PlistInfo? plist = null;
        try
        {
            plist = await plistReader.ReadAsync(bundlePath, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read plist for {Bundle}", bundlePath);
        }

        var name = plist?.DisplayName?.Trim() ?? Path.GetFileNameWithoutExtension(bundlePath);

        if (string.IsNullOrWhiteSpace(name))
        {
            logger.LogDebug("Skipping bundle with no name: {Bundle}", bundlePath);
            return null;
        }

        var version = plist?.ShortVersion?.Trim() ?? plist?.BundleVersion?.Trim();
        var buildVersion = plist?.BundleVersion?.Trim();
        var bundleId = plist?.BundleIdentifier?.Trim();

        // System app: physically lives under /System/Applications OR has a com.apple.* bundle ID.
        var isSystemApp = rootIsSystem || IsAppleBundleId(bundleId);
        var kind = isSystemApp ? AppKind.SystemApp : AppKind.App;

        UpdateMethod? suggestedMethod = isSystemApp ? UpdateMethod.None : null;
        string? suggestedDetail = null;
        string? suFeedUrl = null;

        if (!isSystemApp)
        {
            if (IsWebApp(bundleId))
            {
                // PWA / browser-hosted web app: the browser manages updates, no external check needed
                suggestedMethod = UpdateMethod.SelfUpdate;
            }
            else if (IsMasInstalled(bundlePath))
            {
                // AppStore (priority 1) beats Sparkle (priority 4): prefer App Store even when
                // the bundle also advertises a Sparkle feed.
                suggestedMethod = UpdateMethod.AppStore;
            }
            else if (!string.IsNullOrWhiteSpace(plist?.SparkleUrl))
            {
                suggestedMethod = UpdateMethod.Sparkle;
                suggestedDetail = plist.SparkleUrl;
                suFeedUrl = plist.SparkleUrl;
            }
        }

        logger.LogDebug(
            "Discovered {Kind} {Name} v{Version} [{BundleId}] at {Path}",
            kind, name, version ?? "?", bundleId ?? "—", bundlePath);

        return new DiscoveredApp(
            name,
            new AppIdentifier(Name, DisplayName),
            kind,
            version,
            bundlePath,
            bundleId,
            SuggestedMethod: suggestedMethod,
            SuggestedMethodDetail: suggestedDetail,
            SuFeedUrl: suFeedUrl,
            InstalledBuildVersion: buildVersion);
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
}