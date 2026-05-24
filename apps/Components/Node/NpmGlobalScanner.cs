using System.Runtime.CompilerServices;
using System.Text.Json;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Node;

/// <summary>
/// Discovers globally installed npm packages via <c>npm list -g --depth=0 --json</c>.
/// Each entry is emitted as <see cref="AppKind.Package"/>.
/// </summary>
public sealed class NpmGlobalScanner(IProcessRunner runner, ILogger<NpmGlobalScanner> logger)
    : IScanner
{
    public string Name => "npm";

    /// <inheritdoc/>
    public string DisplayName => "npm";

    /// <inheritdoc/>
    public string? GetSourceQualifier(AppKind kind) => kind == AppKind.Packages ? "Package" : null;

    public bool IsAvailable()
    {
        return ScannerHelper.IsExecutableAvailable("npm");
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var npm = ScannerHelper.FindExecutable("npm") ?? "npm";
        var result = await runner.RunAsync(npm, "list -g --depth=0 --json", cancellationToken);

        // npm exits with a non-zero code when it encounters peer-dep warnings even
        // when real output was produced; use stdout if it exists regardless of exit code.
        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            logger.LogWarning("'npm list -g' produced no output. Err: {Err}", result.StandardError.Trim());
            yield break;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(result.StandardOutput);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse 'npm list -g' JSON output");
            yield break;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("dependencies", out var deps))
            {
                yield break;
            }

            foreach (var entry in deps.EnumerateObject())
            {
                var packageName = entry.Name;
                string? version = null;

                if (entry.Value.TryGetProperty("version", out var verProp))
                {
                    version = verProp.GetString();
                }

                yield return new DiscoveredApp(
                    packageName,
                    Name,
                    AppKind.Packages,
                    version,
                    SuggestedMethod: UpdateMethod.PackageRegistry,
                    SuggestedMethodDetail: packageName);
            }
        }
    }
}
