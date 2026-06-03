using System.Diagnostics;

using apps.Components.Audit;

namespace apps;

/// <summary>
/// A discovered app enriched with a resolved update method and check results.
/// Built in-memory from <see cref="App"/> — no database backing.
/// </summary>
[DebuggerDisplay("{App.Name} v{App.InstalledVersion}")]
public sealed class AppRecord(DiscoveredApp app)
{
    /// <summary>
    /// Error message from the most recent update check, or <see langword="null"/> when the
    /// last check succeeded.
    /// </summary>
    public string? LastCheckError { get; init; }

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

    public List<AppRecord>? SubApps => App.SubApps?.Select(c => From(new KeyValuePair<string, DiscoveredApp>(c.Name, c))).ToList() ?? null;

    public DiscoveredApp App { get; init; } = app;

    public bool UpdateAvailable => VersionComparer.IsNewer(App.InstalledVersion, App.LatestVersion);

    /// <summary>Creates an <see cref="AppRecord"/> from a <see cref="App"/>.</summary>
    public static AppRecord From(KeyValuePair<string, DiscoveredApp> kvp) => new(kvp.Value);
}