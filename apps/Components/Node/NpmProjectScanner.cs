using System.Runtime.CompilerServices;
using System.Text.Json;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Node;

/// <summary>
/// Discovers npm dependencies from <c>package.json</c> files anywhere under ~/
/// (excludes node_modules — handled by <see cref="ProjectManifestFinder"/>).
/// Emits entries from both <c>dependencies</c> and <c>devDependencies</c>.
/// Opt-in via <c>--include-project-deps</c>.
/// </summary>
public sealed class NpmProjectScanner(ProjectManifestFinder finder, ILogger<NpmProjectScanner> logger)
    : IProjectLevelScanner
{
    public string Name => "NpmProject";

    /// <inheritdoc/>
    public string DisplayName => "npm";

    public bool IsAvailable()
    {
        return true;
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var manifestPath in finder.FindAsync("package.json", cancellationToken))
        {
            await foreach (var app in ParseManifestAsync(manifestPath, cancellationToken))
            {
                yield return app;
            }
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> ParseManifestAsync(string manifestPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Cannot read {Path}", manifestPath);
            yield break;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Cannot parse {Path}", manifestPath);
            yield break;
        }

        using (doc)
        {
            // Emit from both regular and dev dependencies
            foreach (var sectionName in new[] { "dependencies", "devDependencies" })
            {
                if (!doc.RootElement.TryGetProperty(sectionName, out var section))
                {
                    continue;
                }

                foreach (var dep in section.EnumerateObject())
                {
                    var name = dep.Name;
                    var rawVer = dep.Value.GetString() ?? "";

                    // Normalise version specifier: strip leading ^~>=<
                    var version = rawVer.TrimStart('^', '~', '=', '>', '<', ' ');

                    yield return new DiscoveredApp(
                        name,
                        Name,
                        AppKind.Libraries,
                        string.IsNullOrWhiteSpace(version) ? null : version,
                        ProjectFile: manifestPath,
                        SuggestedMethod: UpdateMethod.PackageRegistry,
                        SuggestedMethodDetail: name);
                }
            }
        }
    }
}
