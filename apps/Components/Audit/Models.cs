using System.Diagnostics;
using System.Text.Json.Serialization;

namespace apps.Components.Audit;

/// <summary>Result of a CVE audit for a single package.</summary>
public sealed class AuditResult
{
    /// <summary>Result of a CVE audit for a single package.</summary>
    public AuditResult(AppRecord app, VulnerabilityInfo[] vulnerabilities)
    {
        App = app;
        Vulnerabilities = vulnerabilities;
    }

    /// <summary>The app that has vulnerabilities.</summary>
    public AppRecord App { get; }

    /// <summary>Known vulnerabilities — mutable so enrichment can add patched version info.</summary>
    public VulnerabilityInfo[] Vulnerabilities
    {
        get;
        private init
        {
            this.App.Vulnerabilities = value;
            field = value;
        }
    }
}

/// <summary>A single vulnerability found for a package.</summary>
public sealed record VulnerabilityInfo(string Id, string? Summary, VulnerabilitySeverity Severity, string? PatchedVersion = null);

/// <summary>Severity level of a vulnerability.</summary>
public enum VulnerabilitySeverity
{
    Unknown,
    Low,
    Medium,
    High,
    Critical
}

public sealed class OsvBatchRequest
{
    [JsonPropertyName("queries")]
    public IReadOnlyList<OsvQuery> Queries { get; init; } = [];
}

[DebuggerDisplay("{Package.Ecosystem}:{Package.Name}@{Version}")]
public sealed class OsvQuery
{
    [JsonPropertyName("package")]
    public required OsvPackage Package { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

[DebuggerDisplay("{Ecosystem}:{Name}")]
public sealed class OsvPackage
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("ecosystem")]
    public required string Ecosystem { get; init; }

    [JsonPropertyName("purl")]
    public string? Purl { get; init; }
}

public sealed class OsvResultEntry
{
    [JsonPropertyName("vulns")]
    public IReadOnlyList<OsvVulnerability>? Vulnerabilities { get; init; }
}

[DebuggerDisplay("{GetDebuggerDisplay(),nq}")]
public sealed class OsvVulnerability
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = null!;

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("details")]
    public string? Details { get; init; }

    [JsonPropertyName("severity")]
    public IReadOnlyList<OsvSeverityEntry>? Severity { get; init; }

    [JsonPropertyName("upstream")]
    public string[]? UpstreamVulnerabilities { get; init; }

    [JsonPropertyName("aliases")]
    public string[]? Aliases { get; init; }

    [JsonPropertyName("affected")]
    public IReadOnlyList<OsvAffectedEntry>? Affected { get; init; }

    public string GetDebuggerDisplay()
    {
        return UpstreamVulnerabilities is { Length: > 0 } ? $"[{UpstreamVulnerabilities[0]}]" : $"[{Id}]";
    }
}

[DebuggerDisplay("{GetDebuggerDisplay(),nq}")]
public sealed class OsvAffectedEntry
{
    [JsonPropertyName("package")]
    public OsvPackage? Package { get; init; }

    [JsonPropertyName("ranges")]
    public OsvVersionRange[]? Ranges { get; init; }

    [JsonPropertyName("versions")]
    public string[]? Versions { get; init; }

    public string GetDebuggerDisplay()
    {
        var display = "";
        if (Package is not null)
        {
            display = $"{Package.Ecosystem}: ";
        }

        if (Versions is null)
        {
            return display + "[unversioned]";
        }

        if (Versions.Length > 3)
        {
            return display + $"[{string.Join(", ", Versions.Take(3))}, ...]";
        }

        return display + $"[{string.Join(", ", Versions)}]";
    }
}

[DebuggerDisplay("{GetDebuggerDisplay(),nq}")]
public sealed class OsvVersionRange
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("events")]
    public IReadOnlyList<OsvVersionEvent>? Events { get; init; }

    public string GetDebuggerDisplay()
    {
        if (Events is { Count: > 0 })
        {
            if (Events.Any(c => c.Fixed is not null))
            {
                return $"Fixed [{string.Join(", ", Events.Where(e => e.Fixed is not null).Select(e => e.Fixed))}]";
            }
        }

        return "Not fixed";
    }
}

[DebuggerDisplay("{GetDebuggerDisplay(),nq}")]
public sealed class OsvVersionEvent
{
    [JsonPropertyName("introduced")]
    public string? Introduced { get; init; }

    [JsonPropertyName("fixed")]
    public string? Fixed { get; init; }

    [JsonPropertyName("last_affected")]
    public string? LastAffected { get; init; }

    [JsonPropertyName("limit")]
    public string? Limit { get; init; }

    public string GetDebuggerDisplay()
    {
        if (Fixed is not null)
        {
            return $"Fixed {Fixed}";
        }

        return $"Not fixed";
    }
}

public sealed class OsvSeverityEntry
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("score")]
    public string? Score { get; init; }
}

public sealed class NistResponse
{
    [JsonPropertyName("vulnerabilities")]
    public NistVulnerability[] Vulnerabilities { get; init; } = [];
}

[DebuggerDisplay("{Cve.Id}")]
public sealed class NistVulnerability
{
    [JsonPropertyName("cve")]
    public NistVulnerabilityCve Cve { get; init; } = null!;
}

[DebuggerDisplay("{Id}")]
public sealed class NistVulnerabilityCve
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = null!;

    [JsonPropertyName("configurations")]
    public NistConfiguration[]? Configurations { get; init; }
}

public sealed class NistConfiguration
{
    [JsonPropertyName("nodes")]
    public NistConfigurationNode[] Nodes { get; init; } = [];
}

public sealed class NistConfigurationNode
{
    [JsonPropertyName("operator")]
    public string Operator { get; init; } = null!;

    [JsonPropertyName("cpeMatch")]
    public NistCpeMatch[] CpeMatch { get; init; } = [];
}

[DebuggerDisplay("{GetDebuggerDisplay(),nq}")]
public sealed class NistCpeMatch
{
    [JsonPropertyName("criteria")]
    public string Criteria { get; init; } = null!;

    [JsonPropertyName("versionStartIncluding")]
    public string? VersionStartIncluding { get; init; }

    [JsonPropertyName("versionStartExcluding")]
    public string? VersionStartExcluding { get; init; }

    [JsonPropertyName("versionEndIncluding")]
    public string? VersionEndIncluding { get; init; }

    [JsonPropertyName("versionEndExcluding")]
    public string? VersionEndExcluding { get; init; }

    public string? GetDebuggerDisplay()
    {
        string? output = null;
        if (VersionStartIncluding is not null)
        {
            output += $"{VersionStartIncluding} <= x";
        }

        if (VersionStartIncluding is null && VersionStartExcluding is not null)
        {
            output += $"x < {VersionStartExcluding}";
        }

        if (VersionEndExcluding is not null)
        {
            if (VersionStartIncluding is null)
            {
                output += $"x";
            }

            output += $" < {VersionEndExcluding}";
        }

        if (VersionEndIncluding is not null)
        {
            if (VersionStartIncluding is null)
            {
                output += $"x";
            }

            output += $" <= {VersionEndIncluding}";
        }

        return output;
    }
}

[JsonSerializable(typeof(NistResponse))]
[JsonSerializable(typeof(OsvBatchRequest))]
[JsonSerializable(typeof(OsvResultEntry))]
public sealed partial class OsvJsonContext : JsonSerializerContext;