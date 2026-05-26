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
    private const string ExecutableName = "node";

    private string? _nodeExecutablePath;
    private string? _nvmExecutablePath;

    public string Name => "Node";

    /// <inheritdoc/>
    public string DisplayName => "Node";

    public OS SupportedOS => OS.MacOS | OS.Windows;

    public bool IsAvailable()
    {
        var path = ScannerHelper.FindExecutable(ExecutableName);
        if (path is not null)
        {
            _nodeExecutablePath = path;
            return true;
        }

        path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nvm", "versions", "node");
        if (Directory.Exists(path))
        {
            _nvmExecutablePath = path;
            return true;
        }

        return false;
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // nvm takes precedence — if nvm manages node we get a richer version list.
        if (_nvmExecutablePath is not null)
        {
            foreach (var app in ScanNvm())
            {
                yield return app;
            }
        }
        else if (_nodeExecutablePath is not null)
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
            versions = Directory.GetDirectories(_nvmExecutablePath!, "v*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cannot list nvm versions in {Dir}", _nvmExecutablePath);
            yield break;
        }

        foreach (var dir in versions)
        {
            var versionTag = Path.GetFileName(dir); // e.g. "v21.7.3"
            var version = versionTag.TrimStart('v');

            yield return new DiscoveredApp(
                ExecutableName,
                new AppIdentifier(Name, DisplayName),
                AppKind.Packages,
                version,
                dir,
                SuggestedMethod: UpdateMethod.Sdk);
        }
    }

    private async Task<DiscoveredApp?> ScanSystemNodeAsync(CancellationToken ct)
    {
        var result = await runner.RunAsync(_nodeExecutablePath!, "--version", ct);
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
            ExecutableName,
            new AppIdentifier(Name, DisplayName),
            AppKind.Packages,
            version,
            _nodeExecutablePath!,
            SuggestedMethod: UpdateMethod.Sdk);
    }
}