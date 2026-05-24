using System.Text.Json.Serialization;

namespace apps.Components.Homebrew;

internal sealed class BrewInfoRoot
{
    [JsonPropertyName("formulae")]
    public BrewFormulaRecord[]? Formulae { get; init; }

    [JsonPropertyName("casks")]
    public BrewCaskRecord[]? Casks { get; init; }
}

internal sealed class BrewFormulaRecord
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("desc")]
    public string? Desc { get; init; }
}

internal sealed class BrewCaskRecord
{
    [JsonPropertyName("token")]
    public string? Token { get; init; }

    /// <summary>Array of human-readable display names (e.g. ["ChatGPT"]).</summary>
    [JsonPropertyName("name")]
    public string[]? Name { get; init; }

    [JsonPropertyName("desc")]
    public string? Desc { get; init; }
}

internal sealed class BrewOutdatedRoot
{
    [JsonPropertyName("formulae")]
    public BrewOutdatedFormula[]? Formulae { get; init; }

    [JsonPropertyName("casks")]
    public BrewOutdatedCask[]? Casks { get; init; }
}

internal sealed class BrewOutdatedFormula
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("current_version")]
    public string? CurrentVersion { get; init; }
}

internal sealed class BrewOutdatedCask
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("current_version")]
    public string? CurrentVersion { get; init; }
}

[JsonSerializable(typeof(BrewInfoRoot))]
[JsonSerializable(typeof(BrewOutdatedRoot))]
internal sealed partial class HomebrewJsonContext : JsonSerializerContext;

