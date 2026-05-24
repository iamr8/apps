using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Swift;

/// <summary>
/// Discovers Swift Package Manager dependencies from <c>Package.swift</c> files
/// anywhere under ~/. Uses regex to extract package URLs and version requirements.
/// Opt-in via <c>--include-project-deps</c>.
/// </summary>
public sealed partial class SwiftPackageScanner(ProjectManifestFinder finder, ILogger<SwiftPackageScanner> logger)
    : IProjectLevelScanner
{
    public string Name => "SwiftPM";

    /// <inheritdoc/>
    public string DisplayName => "Swift PM";

    // Matches .package(url: "URL", from: "1.0.0")
    //      or .package(url: "URL", exact: "1.0.0")
    //      or .package(url: "URL", .upToNextMajor(from: "1.0.0"))
    //      or .package(url: "URL", .upToNextMinor(from: "1.0.0"))
    // IgnoreCase omitted: Swift syntax is case-sensitive (.package must be lowercase).
    [GeneratedRegex(
        """
        \.package\s*\(\s*url\s*:\s*"([^"]+)".*?(?:from|exact)\s*:\s*"([^"]+)"
        """,
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline,
        5_000)]
    private static partial Regex PackageRegex();

    /// <inheritdoc/>
    public bool IsAvailable()
    {
        return true;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var manifestPath in finder.FindAsync("Package.swift", cancellationToken))
        {
            await foreach (var app in ParseManifestAsync(manifestPath, cancellationToken))
            {
                yield return app;
            }
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> ParseManifestAsync(string manifestPath, [EnumeratorCancellation] CancellationToken ct)
    {
        string content;
        try
        {
            content = await File.ReadAllTextAsync(manifestPath, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Cannot read {Path}", manifestPath);
            yield break;
        }

        foreach (Match m in PackageRegex().Matches(content))
        {
            var url = m.Groups[1].Value.Trim();
            var version = m.Groups[2].Value.Trim();

            var name = ExtractNameFromUrl(url);
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
                SuggestedMethod: UpdateMethod.GitHub,
                SuggestedMethodDetail: url);
        }
    }

    /// <summary>Derives a display name from the package repository URL.</summary>
    private static string? ExtractNameFromUrl(string url)
    {
        // "https://github.com/apple/swift-argument-parser.git" → "swift-argument-parser"
        var clean = url.TrimEnd('/');
        if (clean.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[..^4];
        }

        var slash = clean.LastIndexOf('/');
        return slash >= 0 ? clean[(slash + 1)..] : clean;
    }
}
