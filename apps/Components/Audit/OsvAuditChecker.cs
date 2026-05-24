using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Audit;

/// <summary>
/// Queries the OSV.dev API to find known CVEs for discovered packages.
/// Supports NuGet, npm, Go, and PyPI ecosystems.
/// </summary>
public sealed class OsvAuditChecker(IHttpClientFactory httpClientFactory, ILogger<OsvAuditChecker> logger)
{
    private const string OsvApiUrl = "https://api.osv.dev/v1/querybatch";
    private const int BatchSize = 100;

    /// <summary>
    /// Audits all provided app records against OSV.dev for known vulnerabilities.
    /// Returns only records that have at least one vulnerability.
    /// </summary>
    public async Task<IReadOnlyList<AuditResult>> AuditAsync(
        IReadOnlyList<AppRecord> apps,
        Action<int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var auditable = apps
            .Where(a => a.InstalledVersion is not null)
            .Where(a => MapEcosystem(a) is not null)
            .ToArray();

        if (auditable.Length == 0)
        {
            return [];
        }

        var batches = auditable.Chunk(BatchSize).ToArray();
        var results = new List<AuditResult>();
        var completed = 0;

        foreach (var batch in batches)
        {
            var batchResults = await QueryBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            results.AddRange(batchResults);
            completed++;
            onProgress?.Invoke(completed, batches.Length);
        }

        return results;
    }

    private async Task<IReadOnlyList<AuditResult>> QueryBatchAsync(
        AppRecord[] batch,
        CancellationToken cancellationToken)
    {
        var queries = batch.Select(app => new OsvQuery
        {
            Package = new OsvPackage
            {
                Name = GetPackageName(app),
                Ecosystem = MapEcosystem(app)!
            },
            Version = app.InstalledVersion!
        }).ToArray();

        var request = new OsvBatchRequest { Queries = queries };

        try
        {
            using var client = httpClientFactory.CreateClient("osv");

            var response = await client.PostAsJsonAsync(
                OsvApiUrl,
                request,
                OsvJsonContext.Default.OsvBatchRequest,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("OSV API returned {Status}", response.StatusCode);
                return [];
            }

            var result = await response.Content
                .ReadFromJsonAsync(OsvJsonContext.Default.OsvBatchResponse, cancellationToken)
                .ConfigureAwait(false);

            if (result?.Results is null)
            {
                return [];
            }

            var auditResults = new List<AuditResult>();

            for (var i = 0; i < result.Results.Count && i < batch.Length; i++)
            {
                var vulns = result.Results[i].Vulns;
                if (vulns is { Count: > 0 })
                {
                    auditResults.Add(new AuditResult(
                        batch[i],
                        vulns.Select(v => new VulnerabilityInfo(
                            v.Id,
                            v.Summary,
                            MapSeverity(v))).ToArray()));
                }
            }

            return auditResults;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "OSV batch query failed");
            return [];
        }
    }

    private static string GetPackageName(AppRecord app)
    {
        if (app.UpdateMethodDetail is not null && app.UpdateMethod == UpdateMethod.PackageRegistry)
        {
            return app.UpdateMethodDetail;
        }

        return app.Name;
    }

    private static string? MapEcosystem(AppRecord app)
    {
        return app.Scanner switch
        {
            "Dotnet" or "NuGet Global Tools" or "NuGet Local Tools" => "NuGet",
            "npm Global" or "Node" => "npm",
            "Go" or "Go Tools" => "Go",
            "Homebrew" => "OSS-Fuzz",
            _ => null
        };
    }

    private static VulnerabilitySeverity MapSeverity(OsvVuln vuln)
    {
        if (vuln.DatabaseSpecific?.Severity is not null)
        {
            var mapped = ParseSeverityString(vuln.DatabaseSpecific.Severity);
            if (mapped != VulnerabilitySeverity.Unknown)
            {
                return mapped;
            }
        }

        if (vuln.Severity is { Count: > 0 })
        {
            foreach (var entry in vuln.Severity)
            {
                var score = ExtractCvssScore(entry.Score);
                if (score >= 0)
                {
                    return score switch
                    {
                        >= 9.0 => VulnerabilitySeverity.Critical,
                        >= 7.0 => VulnerabilitySeverity.High,
                        >= 4.0 => VulnerabilitySeverity.Medium,
                        > 0 => VulnerabilitySeverity.Low,
                        _ => VulnerabilitySeverity.Unknown
                    };
                }
            }
        }

        return VulnerabilitySeverity.Unknown;
    }

    private static VulnerabilitySeverity ParseSeverityString(string severity)
    {
        return severity.ToUpperInvariant() switch
        {
            "CRITICAL" => VulnerabilitySeverity.Critical,
            "HIGH" => VulnerabilitySeverity.High,
            "MODERATE" or "MEDIUM" => VulnerabilitySeverity.Medium,
            "LOW" => VulnerabilitySeverity.Low,
            _ => VulnerabilitySeverity.Unknown
        };
    }

    private static double ExtractCvssScore(string? vector)
    {
        if (string.IsNullOrWhiteSpace(vector))
        {
            return -1;
        }

        // CVSS vectors end with a numeric score or contain it after the last '/' or ':'
        // Try to parse common patterns like "CVSS:3.1/AV:N/.../Score:7.5" or just "7.5"
        var lastSlash = vector.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < vector.Length - 1)
        {
            var tail = vector[(lastSlash + 1)..];
            if (double.TryParse(tail, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s))
            {
                return s;
            }
        }

        if (double.TryParse(vector, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var score))
        {
            return score;
        }

        return -1;
    }
}

/// <summary>Result of a CVE audit for a single package.</summary>
public sealed record AuditResult(AppRecord App, IReadOnlyList<VulnerabilityInfo> Vulnerabilities);

/// <summary>A single vulnerability found for a package.</summary>
public sealed record VulnerabilityInfo(string Id, string? Summary, VulnerabilitySeverity Severity);

/// <summary>Severity level of a vulnerability.</summary>
public enum VulnerabilitySeverity
{
    Unknown,
    Low,
    Medium,
    High,
    Critical
}

internal sealed class OsvBatchRequest
{
    [JsonPropertyName("queries")]
    public IReadOnlyList<OsvQuery> Queries { get; init; } = [];
}

internal sealed class OsvQuery
{
    [JsonPropertyName("package")]
    public required OsvPackage Package { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

internal sealed class OsvPackage
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("ecosystem")]
    public required string Ecosystem { get; init; }
}

internal sealed class OsvBatchResponse
{
    [JsonPropertyName("results")]
    public IReadOnlyList<OsvResultEntry> Results { get; init; } = [];
}

internal sealed class OsvResultEntry
{
    [JsonPropertyName("vulns")]
    public IReadOnlyList<OsvVuln>? Vulns { get; init; }
}

internal sealed class OsvVuln
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("severity")]
    public IReadOnlyList<OsvSeverityEntry>? Severity { get; init; }

    [JsonPropertyName("database_specific")]
    public OsvDatabaseSpecific? DatabaseSpecific { get; init; }
}

internal sealed class OsvSeverityEntry
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("score")]
    public string? Score { get; init; }
}

internal sealed class OsvDatabaseSpecific
{
    [JsonPropertyName("severity")]
    public string? Severity { get; init; }
}

[JsonSerializable(typeof(OsvBatchRequest))]
[JsonSerializable(typeof(OsvBatchResponse))]
internal sealed partial class OsvJsonContext : JsonSerializerContext;

