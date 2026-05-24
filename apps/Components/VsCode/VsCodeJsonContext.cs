using System.Text.Json.Serialization;

namespace apps.Components.VsCode;

internal sealed class VsCodePackageJson
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }
}

internal sealed class VsCodeQueryRequest
{
    [JsonPropertyName("filters")]
    public VsCodeFilter[]? Filters { get; init; }

    [JsonPropertyName("flags")]
    public int Flags { get; init; }
}

internal sealed class VsCodeFilter
{
    [JsonPropertyName("criteria")]
    public VsCodeCriterion[]? Criteria { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }

    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; init; }
}

internal sealed class VsCodeCriterion
{
    [JsonPropertyName("filterType")]
    public int FilterType { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

internal sealed class VsCodeQueryResponse
{
    [JsonPropertyName("results")]
    public VsCodeQueryResult[]? Results { get; init; }
}

internal sealed class VsCodeQueryResult
{
    [JsonPropertyName("extensions")]
    public VsCodeExtension[]? Extensions { get; init; }
}

internal sealed class VsCodeExtension
{
    [JsonPropertyName("extensionName")]
    public string? ExtensionName { get; init; }

    [JsonPropertyName("publisher")]
    public VsCodePublisher? Publisher { get; init; }

    [JsonPropertyName("versions")]
    public VsCodeExtVersion[]? Versions { get; init; }
}

internal sealed class VsCodePublisher
{
    [JsonPropertyName("publisherName")]
    public string? PublisherName { get; init; }
}

internal sealed class VsCodeExtVersion
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("properties")]
    public VsCodeExtProperty[]? Properties { get; init; }
}

internal sealed class VsCodeExtProperty
{
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

[JsonSerializable(typeof(VsCodePackageJson))]
[JsonSerializable(typeof(VsCodeQueryRequest))]
[JsonSerializable(typeof(VsCodeQueryResponse))]
internal sealed partial class VsCodeJsonContext : JsonSerializerContext;

