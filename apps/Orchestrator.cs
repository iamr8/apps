using System.Diagnostics;

using apps.Components.Audit;

using Microsoft.Extensions.Logging;

namespace apps;

/// <summary>
/// Coordinates the full pipeline: scan → resolve methods → check for updates → render results table.
/// All data flows in-memory; no database is involved.
/// </summary>
public sealed class Orchestrator(
    ScanOrchestrator scanner,
    CheckOrchestrator checker,
    OsvAuditChecker auditor,
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

        var discovered = await scanner.ScanAsync(options.ScopeKind, cancellationToken).ConfigureAwait(false);

        if (options.DryRun)
        {
            var scanned = discovered
                .Where(a => options.ScopeKind is null || a.Value.Kind == options.ScopeKind)
                .Select(AppRecord.From)
                //.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                //.Select(PickBestRecord)
                .OrderBy(a => KindOrder(a.App.Kind)).ThenBy(a => a.App.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            renderer.RenderTable(scanned);
            return 0;
        }

        //
        // renderer.RenderPhaseStart("Resolving update methods…");
        // var resolved = await resolver.InvokeAsync(discovered, cancellationToken).ConfigureAwait(false);
        // renderer.RenderResolverComplete(4, resolved.Count);
        var resolved = discovered.Select(AppRecord.From).ToArray();

        // Mark pinned packages before update checking so checkers can skip them
        foreach (var app in resolved)
        {
            if (pinManager.IsPinned(app.App.Name, app.App.InstalledVersion))
            {
                app.IsPinned = true;
            }
        }

        var (totalChecked, totalUpdates, errors) = await checker.CheckAsync(resolved, cancellationToken).ConfigureAwait(false);

        // await auditor.AuditAsync(resolved, cancellationToken).ConfigureAwait(false);

        var v = resolved
            .Where(a => options.ScopeKind is null || a.App.Kind == options.ScopeKind);
        if (!options.ShowAll)
        {
            v = v.Where(a => a.UpdateAvailable);
        }

        var visible = v
            .OrderBy(a => KindOrder(a.App.Kind)).ThenBy(a => a.App.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        renderer.RenderTable(visible);

        var totalDiscovered = resolved.Length;
        var totalPinned = resolved.Count(a => a.IsPinned);
        var totalVulnerable = resolved.Count(a => a.Vulnerabilities is { Count: > 0 });

        LiveProgressRenderer.RenderSummary(
            discovered: totalDiscovered,
            @checked: totalChecked,
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
            AppKind.Package => 2,
            AppKind.DevTool => 3,
            AppKind.Service => 4,
            _ => 99
        };
    }
}