using System.Runtime.CompilerServices;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Go;

/// <summary>
/// Discovers Go module dependencies from <c>go.mod</c> files anywhere under ~/
/// Opt-in via <c>--include-project-deps</c>.
/// </summary>
public sealed class GoModScanner(ProjectManifestFinder finder, ILogger<GoModScanner> logger)
    : IProjectLevelScanner
{
    public string Name => "GoMod";

    /// <inheritdoc/>
    public string DisplayName => "Go Module";

    public bool IsAvailable()
    {
        return true;
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var manifestPath in finder.FindAsync("go.mod", cancellationToken))
        {
            await foreach (var app in ParseGoModAsync(manifestPath, cancellationToken))
            {
                yield return app;
            }
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> ParseGoModAsync(string manifestPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(manifestPath, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Cannot read {Path}", manifestPath);
            yield break;
        }

        var inRequireBlock = false;

        foreach (var rawLine in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = rawLine.Trim();

            // Strip inline comments
            var commentIdx = line.IndexOf("//", StringComparison.Ordinal);
            if (commentIdx >= 0) line = line[..commentIdx].Trim();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line == "require (")
            {
                inRequireBlock = true;
                continue;
            }

            if (line == ")")
            {
                inRequireBlock = false;
                continue;
            }

            // Inline require: "require module/path v1.2.3"
            if (line.StartsWith("require ", StringComparison.Ordinal) && !line.EndsWith("("))
            {
                var app = ParseRequireLine(line["require ".Length..].Trim(), manifestPath);
                if (app is not null) yield return app;
                continue;
            }

            if (inRequireBlock)
            {
                var app = ParseRequireLine(line, manifestPath);
                if (app is not null) yield return app;
            }
        }
    }

    /// <summary>
    /// Parses "module/path v1.2.3" or "module/path v1.2.3 // indirect"
    /// </summary>
    private static DiscoveredApp? ParseRequireLine(string line, string manifestPath)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var modulePath = parts[0];
        var version = parts[1].TrimStart('v');

        // Display name: last path component of the module path
        var displayName = modulePath.TrimEnd('/');
        var slashIdx = displayName.LastIndexOf('/');
        if (slashIdx >= 0) displayName = displayName[(slashIdx + 1)..];

        return new DiscoveredApp(
            displayName,
            "GoMod",
            AppKind.Libraries,
            version,
            ProjectFile: manifestPath,
            SuggestedMethod: UpdateMethod.PackageRegistry,
            SuggestedMethodDetail: modulePath);
    }
}
