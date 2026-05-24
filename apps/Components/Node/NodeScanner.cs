using System.Runtime.CompilerServices;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Node;

/// <summary>
/// Discovers installed Node.js versions.
/// • System node: reads <c>node --version</c>.
/// • nvm: lists directories under <c>~/.nvm/versions/node/</c> directly
///   (nvm is a shell function, not a standalone binary).
/// </summary>
public sealed class NodeScanner(IProcessRunner runner, ILogger<NodeScanner> logger)
    : IScanner
{
    public string Name => "Node";

    /// <inheritdoc/>
    public string DisplayName => "Node";

    private static readonly string NvmVersionsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nvm", "versions", "node");

    public bool IsAvailable()
    {
        return ScannerHelper.IsExecutableAvailable("node") || Directory.Exists(NvmVersionsDir);
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // nvm takes precedence — if nvm manages node we get a richer version list.
        if (Directory.Exists(NvmVersionsDir))
        {
            foreach (var app in ScanNvm())
            {
                yield return app;
            }
        }
        else if (ScannerHelper.IsExecutableAvailable("node"))
        {
            var app = await ScanSystemNodeAsync(cancellationToken);
            if (app is not null)
            {
                yield return app;
            }
        }
    }


    private IEnumerable<DiscoveredApp> ScanNvm()
    {
        string[] versions;
        try
        {
            versions = Directory.GetDirectories(NvmVersionsDir, "v*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cannot list nvm versions in {Dir}", NvmVersionsDir);
            yield break;
        }

        foreach (var dir in versions)
        {
            var versionTag = Path.GetFileName(dir); // e.g. "v21.7.3"
            var version = versionTag.TrimStart('v');

            yield return new DiscoveredApp(
                "node",
                Name,
                AppKind.Packages,
                version,
                dir,
                SuggestedMethod: UpdateMethod.Sdk);
        }
    }

    private async Task<DiscoveredApp?> ScanSystemNodeAsync(CancellationToken ct)
    {
        var node = ScannerHelper.FindExecutable("node")!;
        var result = await runner.RunAsync(node, "--version", ct);
        if (!result.Success)
        {
            return null;
        }

        // output: "v21.7.3"
        var version = result.StandardOutput.Trim().TrimStart('v');
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return new DiscoveredApp(
            "node",
            Name,
            AppKind.Packages,
            version,
            node,
            SuggestedMethod: UpdateMethod.Sdk);
    }
}
