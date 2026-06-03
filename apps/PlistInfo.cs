namespace apps;

/// <summary>
/// Parsed values from an Info.plist bundle.
/// </summary>
/// <param name="DisplayName">CFBundleDisplayName or CFBundleName — the display name of the app.</param>
/// <param name="ShortVersion">CFBundleShortVersionString — the user-visible version (e.g. "1.2.3").</param>
/// <param name="BundleVersion">CFBundleVersion — the build number (e.g. "12345").</param>
/// <param name="BundleIdentifier">CFBundleIdentifier — reverse-DNS bundle ID (e.g. "com.example.App").</param>
/// <param name="SparkleUrl">SUFeedURL — Sparkle appcast feed URL (present in ~40% of indie apps).</param>
/// <param name="HasSparkleKey">True when Info.plist contains SUPublicEDKey or SUPublicDSAKeyFile (confirms Sparkle 2).</param>
/// <param name="NSExtensionPointIdentifier">
/// Value of <c>NSExtension &gt; NSExtensionPointIdentifier</c>; present only in app-extension bundles (.appex).
/// For example: <c>com.apple.Safari.web-extension</c>.
/// Named after the exact Apple plist key to make the mapping unambiguous.
/// </param>
public sealed record PlistInfo(
    string? DisplayName,
    string? ShortVersion,
    string? BundleVersion,
    string? BundleIdentifier,
    string? SparkleUrl,
    AppAttribute Attribute,
    PlistReader.Plist RawData);