using System.Runtime.CompilerServices;
using System.Text.Json;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Dotnet;

/// <summary>
/// Discovers local .NET tool manifests (<c>.config/dotnet-tools.json</c>) anywhere
/// under the user's home directory and lists the tools they pin.
/// Opt-in: only active when <c>--include-project-deps</c> is passed.
/// </summary>
public sealed class NugetLocalToolsScanner(ProjectManifestFinder finder, ILogger<NugetLocalToolsScanner> logger)
    : IProjectLevelScanner
{
    public string Name => "NugetLocalTools";

    /// <inheritdoc/>
    public string DisplayName => "NuGet";

    /// <inheritdoc/>
    public string? GetSourceQualifier(AppKind kind) => "Local";

    public bool IsAvailable()
    {
        return true;
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var manifestPath in finder.FindAsync("dotnet-tools.json", cancellationToken))
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
            logger.LogDebug(ex, "Failed to read tool manifest: {Path}", manifestPath);
            yield break;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Failed to parse tool manifest JSON: {Path}", manifestPath);
            yield break;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("tools", out var tools))
            {
                yield break;
            }

            foreach (var entry in tools.EnumerateObject())
            {
                var packageId = entry.Name;
                string? version = null;

                if (entry.Value.TryGetProperty("version", out var verProp))
                {
                    version = verProp.GetString();
                }

                yield return new DiscoveredApp(
                    packageId,
                    Name,
                    AppKind.Libraries,
                    version,
                    ProjectFile: manifestPath,
                    SuggestedMethod: UpdateMethod.PackageRegistry,
                    SuggestedMethodDetail: packageId);
            }
        }
    }
}
