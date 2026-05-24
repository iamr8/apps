namespace apps.Models;

/// <summary>
/// Output of a scanner — raw discovery data before DB persistence.
/// </summary>
/// <param name="Name">Display name shown to the user.</param>
/// <param name="Scanner">Scanner that produced this entry (e.g. "Homebrew", "AppStore").</param>
/// <param name="Kind">What kind of thing this is.</param>
/// <param name="InstalledVersion">Currently installed version string (may be null when unknown).</param>
/// <param name="Path">File system path to the binary / bundle / manifest.</param>
/// <param name="BundleId">CFBundleIdentifier for .app bundles; used for App Store matching.</param>
/// <param name="ProjectFile">
/// For project-level deps: absolute path to the manifest file
/// (e.g. *.csproj, go.mod, package.json).
/// </param>
/// <param name="SuggestedMethod">
/// Scanner hint: the update method it already knows about
/// (e.g. HomebrewScanner always sets this to HomebrewFormula/Cask).
/// </param>
/// <param name="SuggestedMethodDetail">
/// Detail for the suggested method (cask name, formula name, registry package id, …).
/// </param>
/// <param name="SuFeedUrl">Sparkle appcast URL from Info.plist SUFeedURL (used by SparkleChecker).</param>
/// <param name="Description">
/// Short human-readable description shown as a dim subtitle in the table.
/// For VS Code extensions: the marketplace display name.
/// For Homebrew: the formula/cask description.
/// </param>
/// <param name="Digest">
/// Content-addressed sha256 digest for image-based artifacts (e.g. Docker).
/// Kept separate from <paramref name="InstalledVersion"/> so the display shows a
/// human-readable tag while the checker can compare digests to detect content changes.
/// </param>
/// <param name="Digest">
/// Content-addressed sha256 digest for image-based artifacts (e.g. Docker).
/// Kept separate from <paramref name="InstalledVersion"/> so the display shows a
/// human-readable tag while the checker can compare digests to detect content changes.
/// </param>
/// <param name="InstalledBuildVersion">
/// <c>CFBundleVersion</c> (build number) for .app bundles — distinct from
/// <paramref name="InstalledVersion"/> which carries <c>CFBundleShortVersionString</c>.
/// Used by <c>SparkleChecker</c> to detect intra-release updates where the marketing
/// version is unchanged but the build number has advanced (e.g. Telegram 12.7 build 281567 → 281596).
/// </param>
public sealed record DiscoveredApp(
    string Name,
    string Scanner,
    AppKind Kind,
    string? InstalledVersion,
    string? Path = null,
    string? BundleId = null,
    string? ProjectFile = null,
    UpdateMethod? SuggestedMethod = null,
    string? SuggestedMethodDetail = null,
    string? SuFeedUrl = null,
    string? Description = null,
    string? Digest = null,
    string? InstalledBuildVersion = null
);