using apps.Components.Audit;

namespace apps.Models;

/// <summary>
/// A discovered app enriched with a resolved update method and check results.
/// Built in-memory from <see cref="DiscoveredApp"/> — no database backing.
/// </summary>
public sealed class AppRecord
{
    public required string Name { get; init; }
    public string? BundleId { get; init; }
    public string? InstalledVersion { get; set; }
    /// <summary>
    /// <c>CFBundleVersion</c> build number, distinct from <see cref="InstalledVersion"/>
    /// (<c>CFBundleShortVersionString</c>). Non-null only for .app bundles where the plist
    /// exposes both keys. Used by <c>SparkleChecker</c> for intra-release build comparisons.
    /// </summary>
    public string? InstalledBuildVersion { get; set; }
    public string? Path { get; init; }
    public required AppIdentifier Identifier { get; init; }

    /// <summary>app | devtool | package | dep | service</summary>
    public AppKind Kind { get; init; }

    /// <summary>
    /// Resolved update channel. Null means no channel could be determined.
    /// </summary>
    public UpdateMethod? UpdateMethod { get; set; }

    /// <summary>
    /// Context for the update method: cask name, GitHub repo (owner/repo),
    /// Sparkle feed URL, registry package id, etc.
    /// </summary>
    public string? UpdateMethodDetail { get; set; }

    /// <summary>For project-level deps: absolute path to the manifest file.</summary>
    public string? ProjectFile { get; init; }

    public string? LatestVersion { get; set; }
    public bool UpdateAvailable { get; set; }

    /// <summary>
    /// Short human-readable description used as a dim subtitle in the table.
    /// For VS Code extensions: the marketplace display name (Name holds the extension ID).
    /// For Homebrew: the formula/cask description text.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Content-addressed sha256 digest for image-based artifacts (e.g. Docker).
    /// Never displayed; used only by the checker to detect content changes behind a stable tag.
    /// </summary>
    public string? Digest { get; init; }

    /// <summary>
    /// Error message from the most recent update check, or <see langword="null"/> when the
    /// last check succeeded.
    /// </summary>
    public string? LastCheckError { get; set; }

    /// <summary>
    /// Known CVE vulnerabilities found for this package during a <c>--check</c> audit.
    /// Empty when no audit was run or no vulnerabilities were found.
    /// </summary>
    public IReadOnlyList<VulnerabilityInfo>? Vulnerabilities { get; set; }

    /// <summary>
    /// When <c>true</c>, the package is pinned at its current version.
    /// Pinned packages are shown in output but update checking is suppressed for them.
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>Creates an <see cref="AppRecord"/> from a <see cref="DiscoveredApp"/>.</summary>
    public static AppRecord From(DiscoveredApp app) => new()
    {
        Name = app.Name,
        BundleId = app.BundleId,
        InstalledVersion = app.InstalledVersion,
        InstalledBuildVersion = app.InstalledBuildVersion,
        Path = app.Path,
        Identifier = app.Identifier,
        Kind = app.Kind,
        UpdateMethod = app.SuggestedMethod,
        UpdateMethodDetail = app.SuggestedMethodDetail,
        ProjectFile = app.ProjectFile,
        Description = app.Description,
        Digest = app.Digest
    };
}