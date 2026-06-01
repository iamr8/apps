using apps.Components;

namespace apps.Models;

/// <summary>
/// Output of a scanner — raw discovery data before DB persistence.
/// </summary>
public sealed record DiscoveredApp
{
    /// <summary>
    /// Output of a scanner — raw discovery data before DB persistence.
    /// </summary>
    public DiscoveredApp(IScanner source, string name, AppIdentifier identifier, AppKind kind)
    {
        Source = source;
        this.Name = name;
        this.Identifier = identifier;
        this.Kind = kind;
    }

    public IScanner Source { get; }

    /// <summary>Display name shown to the user.</summary>
    public string Name { get; init; }

    public string? PackageId { get; init; }

    public AppIdentifier Identifier { get; set; }

    /// <summary>What kind of thing this is.</summary>
    public AppKind Kind { get; init; }

    /// <summary>Currently installed version string (may be null when unknown).</summary>
    public string InstalledVersion { get; init; }

    /// <summary>
    /// <c>CFBundleVersion</c> (build number) for .app bundles — distinct from
    /// <see name="InstalledVersion"/> which carries <c>CFBundleShortVersionString</c>.
    /// Used by <c>SparkleChecker</c> to detect intra-release updates where the marketing
    /// version is unchanged but the build number has advanced (e.g. Telegram 12.7 build 281567 → 281596).
    /// </summary>
    public string? InstalledBuildNumber { get; init; }

    /// <summary> Latest version string (may be null when unknown).</summary>
    public string? LatestVersion { get; set; }

    public string? LatestBuildNumber { get; set; }

    /// <summary>File system path to the binary / bundle / manifest.</summary>
    public string? Path { get; init; }

    /// <summary>CFBundleIdentifier for .app bundles; used for App Store matching.</summary>
    public string? BundleId { get; set; }

    /// <summary>
    /// For project-level deps: absolute path to the manifest file
    /// (e.g. *.csproj, go.mod, package.json).
    /// </summary>
    public string? ProjectFile { get; init; }

    /// <summary>
    /// Scanner hint: the update method it already knows about
    /// (e.g. HomebrewScanner always sets this to HomebrewFormula/Cask).
    /// </summary>
    public UpdateMethod? UpdateMethod { get; set; }

    /// <summary>
    /// Detail for the suggested method (cask name, formula name, registry package id, …).
    /// </summary>
    public string? UpdateMethodDetail { get; set; }

    /// <summary>Sparkle appcast URL from Info.plist SUFeedURL (used by SparkleChecker).</summary>
    public string? SparkleFeedUrl { get; init; }

    /// <summary>
    /// Short human-readable description shown as a dim subtitle in the table.
    /// For VS Code extensions: the marketplace display name.
    /// For Homebrew: the formula/cask description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Content-addressed sha256 digest for image-based artifacts (e.g. Docker).
    /// Kept separate from <see name="InstalledVersion"/> so the display shows a
    /// human-readable tag while the checker can compare digests to detect content changes.
    /// </summary>
    public string? Digest { get; init; }

    public bool IsDuplicate { get; init; }

    public List<DiscoveredApp>? SubApps { get; set; }

    public bool HasUpdateMethod => this.UpdateMethod is not null || this.SparkleFeedUrl != null || this.SubApps?.Any(b => b.UpdateMethod != null || b.SparkleFeedUrl != null) == true;

    public OsvEcosystemName? OsvEcosystem { get; set; }
}

public record struct AppIdentifier(string Name, string DisplayName, string? Qualifier = null);