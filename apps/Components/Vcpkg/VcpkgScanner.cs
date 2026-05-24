using System.Runtime.CompilerServices;
using System.Text.Json;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Vcpkg;

/// <summary>
/// Discovers C/C++ dependencies from <c>vcpkg.json</c> manifests anywhere under ~/
/// Opt-in via <c>--include-project-deps</c>.
/// </summary>
public sealed class VcpkgScanner(ProjectManifestFinder finder, ILogger<VcpkgScanner> logger)
    : IProjectLevelScanner
{
    public string Name => "vcpkg";

    /// <inheritdoc/>
    public string DisplayName => "vcpkg";

    public bool IsAvailable()
    {
        return true;
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var manifestPath in finder.FindAsync("vcpkg.json", cancellationToken))
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
            logger.LogDebug(ex, "Cannot parse JSON: {Path}", manifestPath);
            yield break;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("dependencies", out var deps))
            {
                yield break;
            }

            foreach (var dep in deps.EnumerateArray())
            {
                string? name = null;
                string? version = null;

                if (dep.ValueKind == JsonValueKind.String)
                {
                    // Simple string dependency: "boost-filesystem"
                    name = dep.GetString();
                }
                else if (dep.ValueKind == JsonValueKind.Object)
                {
                    // Object dependency: { "name": "cpprestsdk", "version>=": "2.10.18" }
                    if (dep.TryGetProperty("name", out var nameProp))
                    {
                        name = nameProp.GetString();
                    }

                    // Look for any version-constraint key
                    foreach (var prop in dep.EnumerateObject())
                    {
                        if (prop.Name.StartsWith("version", StringComparison.OrdinalIgnoreCase))
                        {
                            version = prop.Value.GetString();
                            break;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                yield return new DiscoveredApp(
                    name,
                    Name,
                    AppKind.Libraries,
                    version,
                    ProjectFile: manifestPath,
                    SuggestedMethod: UpdateMethod.PackageRegistry,
                    SuggestedMethodDetail: name);
            }
        }
    }
}
