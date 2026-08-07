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

/// <summary>Request body for the marketplace <c>compatibleUpdates</c> endpoint.</summary>
internal sealed class JetBrainsCompatibleUpdateRequest
{
    [JsonPropertyName("build")]
    public required string Build { get; init; }

    [JsonPropertyName("pluginXMLIds")]
    public required string[] PluginXmlIds { get; init; }
}

/// <summary>
/// One entry of the <c>compatibleUpdates</c> response: the latest plugin version that is
/// compatible with the queried IDE build.
/// </summary>
internal sealed class JetBrainsCompatibleUpdate
{
    [JsonPropertyName("pluginXmlId")]
    public string? PluginXmlId { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

/// <summary>Minimal projection of an IDE's <c>product-info.json</c> used to resolve its build number.</summary>
internal sealed class JetBrainsProductInfo
{
    [JsonPropertyName("productCode")]
    public string? ProductCode { get; init; }

    [JsonPropertyName("dataDirectoryName")]
    public string? DataDirectoryName { get; init; }

    [JsonPropertyName("buildNumber")]
    public string? BuildNumber { get; init; }
}

[JsonSerializable(typeof(JetBrainsPluginUpdate[]))]
[JsonSerializable(typeof(JetBrainsPluginInfo))]
[JsonSerializable(typeof(JetBrainsCompatibleUpdateRequest))]
[JsonSerializable(typeof(JetBrainsCompatibleUpdate[]))]
[JsonSerializable(typeof(JetBrainsProductInfo))]
internal sealed partial class JetBrainsJsonContext : JsonSerializerContext;
