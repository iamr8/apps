using System.Text.Json.Serialization;

namespace apps.Components.Go;

internal sealed class GoModuleLatest
{
    [JsonPropertyName("Version")]
    public string? Version { get; init; }
}

internal sealed class GoRelease
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("stable")]
    public bool Stable { get; init; }
}

[JsonSerializable(typeof(GoModuleLatest))]
[JsonSerializable(typeof(GoRelease[]))]
internal sealed partial class GoJsonContext : JsonSerializerContext;

