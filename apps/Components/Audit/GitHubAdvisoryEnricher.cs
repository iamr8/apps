using System.Net.Http.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace apps.Components.Audit;

/// <summary>
/// Enriches vulnerability results by querying the GitHub Advisory Database REST API
/// for patched version information. The API is public and requires no authentication,
/// but is rate-limited to 60 requests/hour for unauthenticated callers.
/// Only called for packages that already have confirmed GHSA vulnerabilities from OSV.
/// </summary>
public sealed class GitHubAdvisoryEnricher(IHttpClientFactory httpClientFactory, ILogger<GitHubAdvisoryEnricher> logger)
{
    /// <summary>
    /// Enriches audit results with patched version info from the GitHub Advisory Database.
    /// Only fetches details for GHSA-prefixed vulnerability IDs to stay within rate limits.
    /// </summary>
    public async Task EnrichAsync(
        IReadOnlyList<AuditResult> results,
        Action<int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var ghsaIds = results
            .SelectMany(r => r.Vulnerabilities)
            .Where(v => v.Id.StartsWith("GHSA-", StringComparison.OrdinalIgnoreCase))
            .Select(v => v.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (ghsaIds.Length == 0)
        {
            return;
        }

        // Respect rate limit: cap at 30 requests to leave headroom
        var idsToFetch = ghsaIds.Take(30).ToArray();
        var advisoryCache = new Dictionary<string, GitHubAdvisory>(StringComparer.OrdinalIgnoreCase);
        var completed = 0;

        using var client = httpClientFactory.CreateClient("github-advisory");

        foreach (var ghsaId in idsToFetch)
        {
            try
            {
                var advisory = await client
                    .GetFromJsonAsync(
                        $"/advisories/{ghsaId}",
                        GitHubAdvisoryJsonContext.Default.GitHubAdvisory,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (advisory is not null)
                {
                    advisoryCache[ghsaId] = advisory;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Failed to fetch GitHub advisory {Id}", ghsaId);
            }

            completed++;
            onProgress?.Invoke(completed, idsToFetch.Length);
        }

        foreach (var result in results)
        {
            for (var i = 0; i < result.Vulnerabilities.Length; i++)
            {
                var vuln = result.Vulnerabilities[i];

                if (!advisoryCache.TryGetValue(vuln.Id, out var advisory))
                {
                    continue;
                }

                var patchedVersion = advisory.Vulnerabilities?
                    .FirstOrDefault(v => string.Equals(
                        v.Package?.Name,
                        GetPackageName(result.App),
                        StringComparison.OrdinalIgnoreCase))
                    ?.FirstPatchedVersion;

                if (patchedVersion is not null && vuln.PatchedVersion is null)
                {
                    result.Vulnerabilities[i] = vuln with { PatchedVersion = patchedVersion };
                }
            }
        }

        logger.LogDebug(
            "GitHub Advisory enrichment: fetched {Count}/{Total} advisories",
            advisoryCache.Count,
            idsToFetch.Length);
    }

    private static string GetPackageName(Models.AppRecord app)
    {
        if (app.UpdateMethodDetail is not null && app.UpdateMethod == Models.UpdateMethod.PackageRegistry)
        {
            return app.UpdateMethodDetail;
        }

        return app.Name;
    }
}

internal sealed class GitHubAdvisory
{
    [JsonPropertyName("ghsa_id")]
    public string? GhsaId { get; init; }

    [JsonPropertyName("severity")]
    public string? Severity { get; init; }

    [JsonPropertyName("vulnerabilities")]
    public GitHubVulnerability[]? Vulnerabilities { get; init; }
}

internal sealed class GitHubVulnerability
{
    [JsonPropertyName("package")]
    public GitHubPackageRef? Package { get; init; }

    [JsonPropertyName("first_patched_version")]
    public string? FirstPatchedVersion { get; init; }

    [JsonPropertyName("vulnerable_version_range")]
    public string? VulnerableVersionRange { get; init; }
}

internal sealed class GitHubPackageRef
{
    [JsonPropertyName("ecosystem")]
    public string? Ecosystem { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

[JsonSerializable(typeof(GitHubAdvisory))]
internal sealed partial class GitHubAdvisoryJsonContext : JsonSerializerContext;


