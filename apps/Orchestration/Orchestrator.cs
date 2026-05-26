using System.Diagnostics;

using apps.Components.Audit;
using apps.Infrastructure;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Orchestration;

/// <summary>
/// Coordinates the full pipeline: scan → resolve methods → check for updates → render results table.
/// All data flows in-memory; no database is involved.
/// </summary>
public sealed class Orchestrator(
    ScanOrchestrator scanner,
    UpdateMethodResolver resolver,
    CheckOrchestrator checker,
    OsvAuditChecker auditor,
    GitHubAdvisoryEnricher enricher,
    PinManager pinManager,
    LiveProgressRenderer renderer,
    ILogger<Orchestrator> logger)
{
    /// <summary>
    /// Runs the full pipeline: scans all apps, resolves unresolved update methods via
    /// Homebrew/Chocolatey, checks for updates, then renders the results table.
    /// Filters are applied via <paramref name="options"/>.
    /// Returns exit code: 0 = success, 1 = errors encountered.
    /// </summary>
    public async Task<int> InvokeAsync(PipelineOptions options, CancellationToken cancellationToken = default)
    {
        await pinManager.LoadAsync(cancellationToken).ConfigureAwait(false);

        // Handle --pin: pin the named package then exit
        if (options.PinPackage is not null)
        {
            return await HandlePinAsync(options.PinPackage, cancellationToken).ConfigureAwait(false);
        }

        // Handle --unpin: remove a pin then exit
        if (options.UnpinPackage is not null)
        {
            return await HandleUnpinAsync(options.UnpinPackage, cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation("Starting update pipeline (kind={Kind}, dryRun={DryRun})",
            options.ScopeKind,
            options.DryRun);

        var pipelineStopwatch = Stopwatch.StartNew();

        var discovered = await scanner.InvokeAsync(cancellationToken).ConfigureAwait(false);

        if (options.DryRun)
        {
            var scanned = discovered
                .Where(a => a.Kind != AppKind.SystemApp && (options.ScopeKind is null || a.Kind == options.ScopeKind))
                .Select(AppRecord.From)
                .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(PickBestRecord)
                .OrderBy(a => KindOrder(a.Kind)).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            renderer.RenderTable(scanned);
            return 0;
        }

        renderer.RenderPhaseStart("Resolving update methods…");
        var resolved = await resolver.InvokeAsync(discovered, cancellationToken).ConfigureAwait(false);
        renderer.RenderResolverComplete(4, resolved.Count);

        // Mark pinned packages before update checking so checkers can skip them
        foreach (var app in resolved)
        {
            if (pinManager.IsPinned(app.Name, app.InstalledVersion))
            {
                app.IsPinned = true;
            }
        }

        var (_, _, errors) = await checker.InvokeAsync(resolved, cancellationToken).ConfigureAwait(false);

        // Always run CVE audit (skipped only in dry-run mode which returns early above)
        renderer.SetAuditTotal(1);
        using var auditTimerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var auditTimerTask = renderer.RunAuditTimerAsync(auditTimerCts.Token);
        var auditBatchTotal = 1;

        var auditResults = await auditor.AuditAsync(
            resolved,
            (done, total) =>
            {
                auditBatchTotal = total;
                renderer.RenderAuditProgress(done, total);
            },
            cancellationToken).ConfigureAwait(false);

        await auditTimerCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await auditTimerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        renderer.RenderAuditComplete(auditBatchTotal, auditResults.Count);

        // Enrich vulnerabilities with patched version info from GitHub Advisory Database
        if (auditResults.Count > 0)
        {
            await enricher.EnrichAsync(auditResults, null, cancellationToken).ConfigureAwait(false);
        }

        foreach (var result in auditResults)
        {
            result.App.Vulnerabilities = result.Vulnerabilities;
        }

        var outdatedOnly = !options.ShowAll;

        var visible = resolved
            .Where(a => a.Kind != AppKind.SystemApp && (options.ScopeKind is null || a.Kind == options.ScopeKind))
            .Where(a => !outdatedOnly || a.UpdateAvailable || a.IsPinned || a.Vulnerabilities is { Count: > 0 })
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(PickBestRecord)
            .OrderBy(a => KindOrder(a.Kind)).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        renderer.RenderTable(visible);

        var totalUpdates = resolved
            .Where(a => a.Kind != AppKind.SystemApp && a.UpdateAvailable)
            .Select(a => a.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var totalPinned = resolved.Count(a => a.IsPinned);
        var totalVulnerable = resolved.Count(a => a.Vulnerabilities is { Count: > 0 });

        renderer.RenderSummary(
            discovered: resolved.Count(a => a.Kind != AppKind.SystemApp),
            checked_: resolved.Count(a => a.Kind != AppKind.SystemApp && a.UpdateMethod is not null and not UpdateMethod.None),
            updatesAvailable: totalUpdates,
            pinned: totalPinned,
            vulnerabilities: totalVulnerable,
            errors: errors,
            elapsed: pipelineStopwatch.Elapsed);

        return errors > 0 ? 1 : 0;
    }

    private async Task<int> HandlePinAsync(string packageName, CancellationToken cancellationToken)
    {
        // Scan to find the package and its current version
        var discovered = await scanner.FindAsync(packageName, cancellationToken).ConfigureAwait(false);
        if (discovered is null)
        {
            await Console.Error.WriteLineAsync($"Package '{packageName}' not found in scan results.");
            return 1;
        }

        await pinManager.PinAsync(discovered.Name, discovered.InstalledVersion, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Pinned {discovered.Name} @ {discovered.InstalledVersion ?? "any version"}");
        return 0;
    }

    private async Task<int> HandleUnpinAsync(string packageName, CancellationToken cancellationToken)
    {
        await pinManager.UnpinAsync(packageName, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Unpinned {packageName}");
        return 0;
    }

    private static int KindOrder(AppKind kind)
    {
        return kind switch
        {
            AppKind.App => 0,
            AppKind.Extension => 1,
            AppKind.Packages => 2,
            AppKind.Libraries => 3,
            AppKind.Dep => 4,
            AppKind.Service => 5,
            _ => 99
        };
    }

    /// <summary>
    /// Selects the best record from a group of duplicates: highest-priority update method wins;
    /// when priorities tie, prefers the entry that carries a description so metadata is not lost.
    /// </summary>
    private static AppRecord PickBestRecord(IGrouping<string, AppRecord> group)
    {
        var ordered = group.OrderBy(a => (int)(a.UpdateMethod ?? UpdateMethod.None)).ToArray();
        var bestPriority = (int)(ordered[0].UpdateMethod ?? UpdateMethod.None);

        var winner = ordered.FirstOrDefault(a => (int)(a.UpdateMethod ?? UpdateMethod.None) == bestPriority && a.Description is not null) ?? ordered[0];
        if (winner.Description is not null)
        {
            return winner;
        }

        var donor = ordered.FirstOrDefault(a => a.Description is not null);
        if (donor is not null)
        {
            return new AppRecord
            {
                Name = winner.Name,
                BundleId = winner.BundleId,
                InstalledVersion = winner.InstalledVersion,
                InstalledBuildVersion = winner.InstalledBuildVersion,
                Path = winner.Path,
                Identifier = winner.Identifier,
                Kind = winner.Kind,
                UpdateMethod = winner.UpdateMethod,
                UpdateMethodDetail = winner.UpdateMethodDetail,
                ProjectFile = winner.ProjectFile,
                Description = donor.Description,
                Digest = winner.Digest,
                LatestVersion = winner.LatestVersion,
                UpdateAvailable = winner.UpdateAvailable,
                LastCheckError = winner.LastCheckError,
                Vulnerabilities = winner.Vulnerabilities,
                IsPinned = winner.IsPinned
            };
        }

        return winner;
    }
}