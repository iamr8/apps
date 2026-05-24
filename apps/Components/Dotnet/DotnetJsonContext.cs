using System.Text.Json.Serialization;

namespace apps.Components.Dotnet;

internal sealed class DotnetReleasesIndex
{
    [JsonPropertyName("releases-index")]
    public DotnetChannelEntry[]? ReleasesIndex { get; init; }
}

internal sealed class DotnetChannelEntry
{
    [JsonPropertyName("channel-version")]
    public string? ChannelVersion { get; init; }

    [JsonPropertyName("latest-release")]
    public string? LatestRelease { get; init; }

    [JsonPropertyName("latest-sdk")]
    public string? LatestSdk { get; init; }

    [JsonPropertyName("latest-runtime")]
    public string? LatestRuntime { get; init; }

    [JsonPropertyName("support-phase")]
    public string? SupportPhase { get; init; }

    [JsonPropertyName("releases.json")]
    public string? ReleasesJsonUrl { get; init; }
}

internal sealed class NugetVersionIndex
{
    [JsonPropertyName("versions")]
    public string[]? Versions { get; init; }
}

[JsonSerializable(typeof(DotnetReleasesIndex))]
[JsonSerializable(typeof(NugetVersionIndex))]
internal sealed partial class DotnetJsonContext : JsonSerializerContext;

