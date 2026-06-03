using System.Diagnostics;
using System.Text.Json.Serialization;

namespace apps.Components.MacOs;

public enum AppPlatform
{
    NativeMac,
    IosOnMac
}

public sealed class ItunesLookupResponse
{
    [JsonPropertyName("resultCount")]
    public int ResultCount { get; init; }

    [JsonPropertyName("results")]
    public ItunesResult[]? Results { get; init; }
}

public sealed class ItunesResult
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("trackId")]
    public long TrackId { get; init; }

    [JsonPropertyName("trackName")]
    public string? TrackName { get; init; }

    [JsonPropertyName("bundleId")]
    public string? BundleId { get; init; }

    /// <summary>
    /// Platform discriminator: <c>"mac-software"</c> for native macOS apps,
    /// <c>"software"</c> for iOS apps. Universal apps that share a bundle ID
    /// often return the iOS record whose version may differ from the macOS build.
    /// </summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("features")]
    public string[]? Features { get; init; }

    [JsonPropertyName("supportedDevices")]
    public string[]? SupportedDevices { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("wrapperType")]
    public string? WrapperType { get; init; }
}

public sealed class BrewInfoRoot
{
    [JsonPropertyName("formulae")]
    public BrewFormulaRecord[] Formulae { get; init; } = [];

    [JsonPropertyName("casks")]
    public BrewCaskRecord[] Casks { get; init; } = [];
}

[DebuggerDisplay("{Name}: {Description}")]
public sealed class BrewFormulaRecord
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = null!;

    [JsonPropertyName("full_name")]
    public string FullName { get; init; } = null!;

    [JsonPropertyName("desc")]
    public string Description { get; init; } = null!;

    [JsonPropertyName("versions")]
    public BrewFormulaVersion LatestVersion { get; init; } = null!;

    [JsonPropertyName("installed")]
    public BrewFormulaInstalled[] InstalledVersion { get; init; } = [];

    public bool IsOutdated => InstalledVersion.Length > 0 && !string.Equals(LatestVersion.StableVersion, InstalledVersion[0].Version, StringComparison.OrdinalIgnoreCase);
}

[DebuggerDisplay("{StableVersion}")]
public class BrewFormulaVersion
{
    [JsonPropertyName("stable")]
    public string StableVersion { get; init; } = null!;
}

[DebuggerDisplay("{Version}")]
public class BrewFormulaInstalled
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = null!;
}

[DebuggerDisplay("{Name}: {Description}")]
public sealed class BrewCaskRecord
{
    [JsonPropertyName("token")]
    public string Token { get; init; } = null!;

    /// <summary>Array of human-readable display names (e.g. ["ChatGPT"]).</summary>
    [JsonPropertyName("name")]
    public string[] Name { get; init; } = [];

    [JsonPropertyName("desc")]
    public string Description { get; init; } = null!;

    [JsonPropertyName("version")]
    public string LatestVersion { get; set; } = null!;

    [JsonPropertyName("installed")]
    public string InstalledVersion { get; init; } = null!;

    [JsonPropertyName("artifacts")]
    public BrewCaskArtifact[]? Artifacts { get; init; }
}

[DebuggerDisplay("{Target}")]
public sealed class BrewCaskArtifact
{
    [JsonPropertyName("app")]
    public string[]? App { get; init; }

    [JsonPropertyName("uninstall")]
    public Dictionary<string, string[]>[]? Uninstall { get; init; }

    [JsonPropertyName("target")]
    public string? Target { get; init; }
}

/// <summary>Minimal projection of <c>https://formulae.brew.sh/api/cask/{token}.json</c>.</summary>
[DebuggerDisplay("{Version}")]
public sealed class BrewCaskApiResponse
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

[JsonSerializable(typeof(ItunesLookupResponse))]
[JsonSerializable(typeof(BrewInfoRoot))]
[JsonSerializable(typeof(BrewCaskApiResponse))]
public sealed partial class MacOsApplicationsJsonContext : JsonSerializerContext;