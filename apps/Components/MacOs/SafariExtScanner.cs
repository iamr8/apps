using System.Runtime.CompilerServices;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.MacOs;

/// <summary>
/// Discovers Safari extensions embedded as <c>.appex</c> plug-ins inside installed <c>.app</c> bundles.
/// Each matching plug-in is emitted as <see cref="AppKind.Extension"/>.
/// An extension's update method inherits from its parent app:
/// <see cref="UpdateMethod.AppStore"/> when the parent carries a MAS receipt,
/// otherwise <see cref="UpdateMethod.SelfUpdate"/> (the host app updates the extension).
/// </summary>
public sealed class SafariExtScanner(PlistReader plistReader, ILogger<SafariExtScanner> logger)
    : IScanner
{
    public string Name => "SafariExt";

    /// <inheritdoc/>
    public string DisplayName => "Safari";

    /// <inheritdoc/>
    /// <remarks>All apps from this scanner are extensions; the qualifier is always "Extension".</remarks>
    public string? GetSourceQualifier(AppKind kind) => "Extension";

    private static readonly string[] AppRoots =
    [
        "/Applications",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"),
        "/Applications/Utilities",
        "/System/Applications"
    ];

    /// <inheritdoc/>
    public bool IsAvailable() => true;

    /// <inheritdoc/>
    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var root in AppRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> apps;
            try
            {
                apps = Directory.EnumerateDirectories(root, "*.app", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cannot enumerate directory: {Root}", root);
                continue;
            }

            foreach (var appPath in apps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await foreach (var ext in ScanAppBundleAsync(appPath, cancellationToken).ConfigureAwait(false))
                {
                    yield return ext;
                }
            }
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> ScanAppBundleAsync(
        string appPath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pluginsDir = Path.Combine(appPath, "Contents", "PlugIns");
        if (!Directory.Exists(pluginsDir))
        {
            yield break;
        }

        IEnumerable<string> appexBundles;
        try
        {
            appexBundles = Directory.EnumerateDirectories(pluginsDir, "*.appex", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Cannot enumerate PlugIns directory: {Dir}", pluginsDir);
            yield break;
        }

        var parentAppName = Path.GetFileNameWithoutExtension(appPath);
        var hasMasReceipt = IsMasInstalled(appPath);

        foreach (var appexPath in appexBundles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var app = await BuildExtensionAppAsync(appexPath, parentAppName, hasMasReceipt, cancellationToken)
                .ConfigureAwait(false);

            if (app is not null)
            {
                yield return app;
            }
        }
    }

    private async Task<DiscoveredApp?> BuildExtensionAppAsync(
        string appexPath,
        string parentAppName,
        bool parentHasMasReceipt,
        CancellationToken cancellationToken)
    {
        PlistInfo? plist = null;
        try
        {
            plist = await plistReader.ReadAsync(appexPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read plist for {Appex}", appexPath);
        }

        if (!IsSafariExtension(plist?.NSExtensionPointIdentifier))
        {
            return null;
        }

        var name = plist?.DisplayName?.Trim() ?? Path.GetFileNameWithoutExtension(appexPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var version = plist?.ShortVersion?.Trim() ?? plist?.BundleVersion?.Trim();
        var bundleId = plist?.BundleIdentifier?.Trim();
        var updateMethod = parentHasMasReceipt ? UpdateMethod.AppStore : UpdateMethod.SelfUpdate;

        logger.LogDebug(
            "Discovered Safari extension {Name} v{Version} [{BundleId}] inside {Parent}",
            name, version ?? "?", bundleId ?? "—", parentAppName);

        return new DiscoveredApp(
            name,
            Name,
            AppKind.Extension,
            version,
            appexPath,
            bundleId,
            SuggestedMethod: updateMethod,
            Description: $"Safari Extension · {parentAppName}");
    }

    private static bool IsSafariExtension(string? pointIdentifier) =>
        pointIdentifier is "com.apple.Safari.extension" or "com.apple.Safari.web-extension";

    private static bool IsMasInstalled(string bundlePath) =>
        Directory.Exists(Path.Combine(bundlePath, "Contents", "_MASReceipt"));
}

