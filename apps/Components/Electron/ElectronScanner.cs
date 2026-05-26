using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Electron;

/// <summary>
/// Discovers Electron apps that ship <c>Contents/Resources/app-update.yml</c>,
/// which specifies the auto-update channel used by <c>electron-updater</c>.
///
/// <para>Detection criteria (both must be true):</para>
/// <list type="bullet">
///   <item><c>Contents/Frameworks/Electron Framework.framework</c> directory exists.</item>
///   <item><c>Contents/Resources/app-update.yml</c> is present and contains a supported provider.</item>
/// </list>
///
/// <para>
/// Emits <see cref="AppKind.App"/> with <see cref="UpdateMethod.Electron"/>.
/// <see cref="DiscoveredApp.SuggestedMethodDetail"/> is encoded as:
/// </para>
/// <list type="bullet">
///   <item><c>"github:{owner}/{repo}"</c> — GitHub Releases provider.</item>
///   <item><c>"generic:{url}"</c> — self-hosted generic feed; checker fetches <c>{url}/latest-mac.yml</c>.</item>
/// </list>
///
/// <para>
/// Runs alongside <see cref="ApplicationsScanner"/>. Because the DB upsert uses
/// <c>COALESCE(existing, incoming)</c> per scanner column the two produce separate rows;
/// the <c>--show-all</c> deduplication keeps only the highest-priority row per name,
/// which will be <see cref="UpdateMethod.AppStore"/> (priority 1) when the app also
/// has an MAS receipt, or <see cref="UpdateMethod.Electron"/> (priority 5) otherwise.
/// </para>
/// </summary>
public sealed class ElectronScanner(PlistReader plistReader, ILogger<ElectronScanner> logger)
    : IScanner
{
    private string[] _executablePaths = [];

    public string Name => "Electron";

    /// <inheritdoc/>
    /// <remarks>Electron apps live in /Applications and share the same source label as regular app bundles.</remarks>
    public string DisplayName => "Applications";

    public OS SupportedOS => OS.MacOS | OS.Windows;

    public bool IsAvailable()
    {
        string[] scanRoots =
        [
            "/Applications",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"),
            "/Applications/Utilities"
        ];

        _executablePaths = scanRoots.Where(Directory.Exists).ToArray();
        return _executablePaths.Length > 0;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var root in _executablePaths)
        {
            IEnumerable<string> bundles;
            try
            {
                bundles = Directory.EnumerateDirectories(root, "*.app", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cannot enumerate directory: {Root}", root);
                continue;
            }

            foreach (var bundlePath in bundles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var app = await TryBuildAsync(bundlePath, cancellationToken).ConfigureAwait(false);
                if (app is not null)
                {
                    yield return app;
                }
            }
        }
    }

    private async Task<DiscoveredApp?> TryBuildAsync(string bundlePath, CancellationToken cancellationToken)
    {
        // Electron apps always ship this framework directory
        var frameworkPath = Path.Combine(bundlePath, "Contents", "Frameworks", "Electron Framework.framework");
        if (!Directory.Exists(frameworkPath))
        {
            return null;
        }

        var ymlPath = Path.Combine(bundlePath, "Contents", "Resources", "app-update.yml");
        if (!File.Exists(ymlPath))
        {
            return null;
        }

        var (provider, owner, repo, url) = await ParseYmlAsync(ymlPath, cancellationToken).ConfigureAwait(false);

        var methodDetail = provider switch
        {
            "github" when owner is not null && repo is not null => $"github:{owner}/{repo}",
            "generic" when url is not null => $"generic:{url}",
            _ => null
        };

        if (methodDetail is null)
        {
            logger.LogDebug(
                "ElectronScanner: skipping {Bundle} — unsupported or incomplete provider '{Provider}'",
                bundlePath, provider ?? "(none)");
            return null;
        }

        PlistInfo? plist = null;
        try
        {
            plist = await plistReader.ReadAsync(bundlePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read plist for {Bundle}", bundlePath);
        }

        var name = plist?.DisplayName?.Trim() ?? Path.GetFileNameWithoutExtension(bundlePath);
        var version = plist?.ShortVersion?.Trim() ?? plist?.BundleVersion?.Trim();
        var bundleId = plist?.BundleIdentifier?.Trim();

        logger.LogDebug(
            "ElectronScanner: {Name} v{Version} [{Provider}] at {Path}",
            name, version ?? "?", provider, bundlePath);

        return new DiscoveredApp(
            name,
            new AppIdentifier(Name, DisplayName, "Application"),
            AppKind.App,
            version,
            bundlePath,
            bundleId,
            SuggestedMethod: UpdateMethod.Electron,
            SuggestedMethodDetail: methodDetail);
    }

    /// <summary>
    /// Parses <c>app-update.yml</c> line-by-line without a YAML library (AOT-safe).
    /// Only the four keys meaningful for update resolution are extracted.
    /// </summary>
    private static async Task<(string? Provider, string? Owner, string? Repo, string? Url)>
        ParseYmlAsync(string path, CancellationToken cancellationToken)
    {
        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return (null, null, null, null);
        }

        string? provider = null, owner = null, repo = null, url = null;

        foreach (var raw in lines)
        {
            var colonIdx = raw.IndexOf(':');
            if (colonIdx < 0)
            {
                continue;
            }

            var key = raw[..colonIdx].Trim();
            var value = raw[(colonIdx + 1)..].Trim();

            switch (key)
            {
                case "provider": provider = value; break;
                case "owner": owner = value; break;
                case "repo": repo = value; break;
                case "url": url = value; break;
            }
        }

        return (provider, owner, repo, url);
    }
}