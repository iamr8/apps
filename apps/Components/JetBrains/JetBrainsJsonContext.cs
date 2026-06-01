using System.Text.Json.Serialization;

namespace apps.Components.JetBrains;

internal sealed class JetBrainsPluginUpdate
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

/// <summary>Minimal projection of the JetBrains marketplace plugin search response.</summary>
internal sealed class JetBrainsPluginInfo
{
    [JsonPropertyName("id")]
    public int? Id { get; init; }
}

[JsonSerializable(typeof(JetBrainsPluginUpdate[]))]
[JsonSerializable(typeof(JetBrainsPluginInfo[]))]
internal sealed partial class JetBrainsJsonContext : JsonSerializerContext;

