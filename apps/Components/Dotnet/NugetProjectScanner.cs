using System.Runtime.CompilerServices;
using System.Xml;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Dotnet;

/// <summary>
/// Discovers NuGet package references in project files (*.csproj, *.fsproj,
/// Directory.Packages.props) anywhere under the user's home directory.
/// Opt-in: only active when <c>--include-project-deps</c> is passed.
/// </summary>
public sealed class NugetProjectScanner(ProjectManifestFinder finder, ILogger<NugetProjectScanner> logger)
    : IProjectLevelScanner
{
    public string Name => "NugetProject";

    /// <inheritdoc/>
    public string DisplayName => "NuGet";

    // File patterns that may contain PackageReference / PackageVersion elements
    private static readonly string[] ManifestPatterns = ["*.csproj", "*.fsproj", "Directory.Packages.props"];

    public bool IsAvailable()
    {
        return true;
        // always register; ScanOrchestrator gates on IProjectLevelScanner
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var pattern in ManifestPatterns)
        {
            await foreach (var manifestPath in finder.FindAsync(pattern, cancellationToken))
            {
                await foreach (var app in ParseManifestAsync(manifestPath, cancellationToken))
                {
                    yield return app;
                }
            }
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> ParseManifestAsync(string manifestPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string xml;
        try
        {
            xml = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read manifest: {Path}", manifestPath);
            yield break;
        }

        XmlDocument doc;
        try
        {
            doc = new XmlDocument();
            doc.LoadXml(xml);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to parse XML: {Path}", manifestPath);
            yield break;
        }

        // Match both <PackageReference> (project files) and <PackageVersion> (Central Package Management)
        var nodes = doc.SelectNodes("//*[local-name()='PackageReference' or local-name()='PackageVersion']");
        if (nodes is null)
        {
            yield break;
        }

        foreach (XmlNode node in nodes)
        {
            var include = node.Attributes?["Include"]?.Value?.Trim();
            var version = node.Attributes?["Version"]?.Value?.Trim()
                          ?? node.SelectSingleNode("*[local-name()='Version']")?.InnerText?.Trim();

            if (string.IsNullOrWhiteSpace(include)) continue;

            yield return new DiscoveredApp(
                include,
                Name,
                AppKind.Libraries,
                string.IsNullOrWhiteSpace(version) ? null : version,
                ProjectFile: manifestPath,
                SuggestedMethod: UpdateMethod.PackageRegistry,
                SuggestedMethodDetail: include);
        }
    }
}
