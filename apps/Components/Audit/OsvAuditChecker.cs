using System.Text.Json;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

namespace apps.Components.Audit;

/// <summary>
/// Queries the OSV.dev API to find known CVEs for discovered packages.
/// Supports NuGet, npm, Go, and PyPI ecosystems.
/// </summary>
public sealed class OsvAuditChecker(IHttpClientFactory httpClientFactory, LiveProgressRenderer renderer, ILogger<OsvAuditChecker> logger)
{
    /// <summary>
    /// Audits all provided app records against OSV.dev for known vulnerabilities.
    /// Returns only records that have at least one vulnerability.
    /// </summary>
    public async Task AuditAsync(IReadOnlyList<AppRecord> apps, CancellationToken cancellationToken = default)
    {
        var auditableApps = apps
            .Where(c => c.App.OsvEcosystem == OsvEcosystemName.None)
            .ToArray();
        if (auditableApps.Length == 0)
        {
            return;
        }

        renderer.SetAuditTotal(auditableApps.Length);
        using var auditTimerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var auditTimerTask = renderer.RunAuditTimerAsync(auditTimerCts.Token);
        var auditBatchTotal = 1;

        var results = new List<AuditResult>();
        var completed = 0;

        await foreach (var result in auditableApps.WhenAll<AppRecord, AuditResult>(QueryAsync, cancellationToken: cancellationToken))
        {
            results.Add(result);
            auditBatchTotal = apps.Count;
            renderer.RenderAuditProgress(++completed, apps.Count);
        }

        await auditTimerCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await auditTimerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        renderer.RenderAuditComplete(auditBatchTotal, results.Count(c => c.Vulnerabilities.Length > 0));
    }

    private async Task QueryAsync(AppRecord record, ChannelWriter<AuditResult> writer, CancellationToken cancellationToken)
    {
        var request = new OsvQuery
        {
            Package = new OsvPackage
            {
                Name = GetPackageName(record),
                Ecosystem = MapEcosystem(record)!
            },
            Version = record.App.InstalledVersion!
        };

        try
        {
            using var client = httpClientFactory.CreateClient("osv");
            var requestJson = JsonSerializer.Serialize(request, OsvJsonContext.Default.OsvQuery);
            var stringContent = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/v1/query", stringContent, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("OSV API returned {Status}", response.StatusCode);
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync(stream, OsvJsonContext.Default.OsvResultEntry, cancellationToken).ConfigureAwait(false);
            if (result.Vulnerabilities is null || result.Vulnerabilities.Count == 0)
            {
                await writer.WriteAsync(new AuditResult(record, []), cancellationToken).ConfigureAwait(false);
                return;
            }

            // var fixedVulnerabilities = new List<OsvVulnerability>();
            // foreach (var vulnerability in result.Vulnerabilities)
            // {
            //     var alias = vulnerability.Aliases is { Length: > 0 }
            //         ? vulnerability.Aliases[0]
            //         : vulnerability.UpstreamVulnerabilities is { Length: > 0 }
            //             ? vulnerability.UpstreamVulnerabilities[0]
            //             : vulnerability.Id;
            //     var isVulnerable = await GetNistReferenceAsync(alias, request.Version, cancellationToken);
            //     if (isVulnerable == null)
            //     {
            //         // Couldn't acknowledge the vulnerability, skip it.
            //     }
            //     else if (isVulnerable is true)
            //     {
            //         // Acknowledged as vulnerable, count it and continue to include in results.
            //     }
            //     else
            //     {
            //         // Acknowledged as secure, skip it.
            //         fixedVulnerabilities.Add(vulnerability);
            //     }
            // }
            //
            // var unresolvedVulnerabilities = result.Vulnerabilities.Except(fixedVulnerabilities).ToArray();
            // if (unresolvedVulnerabilities.Length == 0)
            // {
            //     logger.LogDebug("{Package} v{Version} vulnerabilities acknowledged as fixed in NIST CVE database, skipping", record.App.Name, record.App.InstalledVersion);
            //     return;
            // }

            var vulnerabilityMatches = OsvVulnerabilityChecker.FindAffecting(result.Vulnerabilities, request.Package.Name, request.Version, request.Package.Ecosystem);
            if (vulnerabilityMatches.Count == 0)
            {
                await writer.WriteAsync(new AuditResult(record, []), cancellationToken).ConfigureAwait(false);
                logger.LogDebug("{Package} v{Version} not detected as vulnerable in OSV response, skipping", record.App.Name, record.App.InstalledVersion);
                return;
            }

            var vuls = ProjectVulnerabilities(vulnerabilityMatches);

            var auditResult = new AuditResult(record, vuls);
            await writer.WriteAsync(auditResult, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "OSV batch query failed");
        }
    }

    private async Task<bool?> GetNistReferenceAsync(string alias, string installedVersion, CancellationToken cancellationToken)
    {
        try
        {
            // Rate limit without key: 5 requests per rolling 30-second window.
            // With a free API key (apiKey header): 50 / 30s.
            using var client = httpClientFactory.CreateClient("nist");
            var response = await client.GetAsync($"/rest/json/cves/2.0?cveId={alias}", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var nist = await JsonSerializer.DeserializeAsync<NistResponse>(stream, OsvJsonContext.Default.NistResponse, cancellationToken);
            if (nist?.Vulnerabilities is null || nist.Vulnerabilities.Length == 0)
            {
                return null;
            }

            var versions = nist.Vulnerabilities
                .Where(c => c.Cve.Configurations is not null)
                .SelectMany(c => c.Cve.Configurations!)
                .SelectMany(c => c.Nodes)
                .SelectMany(c => c.CpeMatch)
                .Where(c => c.VersionEndExcluding is not null ||
                            c.VersionStartIncluding is not null ||
                            c.VersionStartExcluding is not null ||
                            c.VersionEndIncluding is not null)
                .ToArray();
            if (versions.Length == 0)
            {
                return null;
            }

            var isIncluded = versions.Any(match => (match is { VersionStartIncluding: not null, VersionEndExcluding: not null } && VersionComparer.Compare(installedVersion, match.VersionStartIncluding) >= 0 && VersionComparer.Compare(installedVersion, match.VersionEndExcluding) < 0) ||
                                                   (match is { VersionStartIncluding: not null, VersionEndExcluding: null } && VersionComparer.Compare(installedVersion, match.VersionStartIncluding) >= 0) ||
                                                   (match is { VersionStartIncluding: null, VersionEndExcluding: not null } && VersionComparer.Compare(installedVersion, match.VersionEndExcluding) < 0));
            if (isIncluded)
            {
                // vulnerable
                return true;
            }

            // secure
            return false;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "NIST CVE lookup failed for {Alias}", alias);
            return null;
        }
    }

    /// <summary>
    /// Resolves the package name to query OSV with: the resolved <c>UpdateInfo</c> for library
    /// packages (e.g. the registry id), otherwise the discovered app's display name.
    /// </summary>
    internal static string GetPackageName(AppRecord record)
    {
        if (record.App.UpdateInfo is not null && record.App.Attribute.HasFlag(AppAttribute.Library))
        {
            return record.App.UpdateInfo;
        }

        return record.App.Name;
    }

    /// <summary>
    /// Maps a discovered app's scanner identifier to its OSV ecosystem name, returning an empty
    /// string when the ecosystem is unconstrained (matches any ecosystem during version checks).
    /// </summary>
    internal static string? MapEcosystem(AppRecord record)
    {
        return record.App.Identifier.Name switch
        {
            // "Dotnet" or "NuGet" or "NugetLocalTools" or "NugetProject" => "NuGet",
            // "npm" or "NpmProject" or "Node" => "npm",
            "Go" or "GoTools" or "GoMod" => "Go",
            "SwiftPM" => "SwiftURL",
            _ => ""
        };
    }

    /// <summary>
    /// Projects matched advisories into the reportable vulnerability list, collapsing duplicates
    /// that share the same alias (or id) into a single entry and preferring summary over details.
    /// </summary>
    internal static VulnerabilityInfo[] ProjectVulnerabilities(IReadOnlyList<VulnerabilityMatch> matches)
    {
        return matches
            .GroupBy(c => c.Aliases.FirstOrDefault() ?? c.Id)
            .Select(g =>
            {
                var first = g.First();
                return new VulnerabilityInfo(g.Key, first.Summary ?? first.Details, first.Severity);
            })
            .ToArray();
    }

    private static VulnerabilitySeverity MapSeverity(OsvVulnerability vulnerability)
    {
        if (vulnerability.Severity is { Count: > 0 })
        {
            foreach (var entry in vulnerability.Severity)
            {
                if (entry.Score is null)
                {
                    continue;
                }

                if (entry.Type is not "CVSS_V3")
                {
                    continue;
                }

                var score = CvssV3Calculator.GetSeverityScore(entry.Score);
                if (score >= 0)
                {
                    return score switch
                    {
                        0.0 => VulnerabilitySeverity.Unknown,
                        <= 3.9 => VulnerabilitySeverity.Low,
                        <= 6.9 => VulnerabilitySeverity.Medium,
                        <= 8.9 => VulnerabilitySeverity.High,
                        _ => VulnerabilitySeverity.Critical
                    };
                }
            }
        }

        return VulnerabilitySeverity.Unknown;
    }

    public enum VulnStatus
    {
        Fixed,
        Vulnerable,
        Unknown,
        NotApplicable
    }

    public static VulnStatus CheckVulnerability(OsvVulnerability vuln, string packageName, string ecosystem, string yourVersion, Func<string, string, int> versionCompare)
    {
        if (vuln.Affected is null || vuln.Affected.Count == 0)
        {
            return VulnStatus.Unknown;
        }

        var matches = vuln.Affected
            .Where(a => a.Package.Name == packageName && (ecosystem == "" || a.Package.Ecosystem == ecosystem))
            .ToArray();

        if (matches.Length == 0)
        {
            return VulnStatus.NotApplicable;
        }

        foreach (var affected in matches)
        {
            // Explicit versions list check
            if (affected.Versions?.Contains(yourVersion) == true)
            {
                return VulnStatus.Vulnerable;
            }

            // Walk events
            var vulnerable = false;
            foreach (var range in affected.Ranges)
            {
                foreach (var ev in range.Events)
                {
                    if (ev.Introduced != null)
                    {
                        var introduced = ev.Introduced == "0" ? null : ev.Introduced;
                        if (introduced == null || versionCompare(yourVersion, introduced) >= 0)
                        {
                            vulnerable = true;
                        }
                    }
                    else if (ev.Fixed != null)
                    {
                        if (versionCompare(yourVersion, ev.Fixed) >= 0)
                        {
                            vulnerable = false;
                        }
                    }
                }
            }

            if (vulnerable)
            {
                return VulnStatus.Vulnerable;
            }
        }

        return VulnStatus.Fixed;
    }
}