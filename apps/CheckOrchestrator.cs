using System.Threading.Channels;

using apps.Components;

using Microsoft.Extensions.Logging;

namespace apps;

/// <summary>
/// Stage 2 of the pipeline: groups apps by their resolved UpdateMethod,
/// starts all checker groups concurrently, and streams results through a
/// bounded channel. Each result is applied back to its <see cref="AppRecord"/>
/// in-memory so the caller can inspect update status after this stage.
///
/// Results print to the terminal as each check completes — no waiting for
/// the full batch.
/// </summary>
public sealed class CheckOrchestrator(IEnumerable<IScanner> scanners, LiveProgressRenderer renderer, ILogger<CheckOrchestrator> logger)
{
    /// <summary>
    /// Groups apps by update method, fans out all checkers concurrently, applies results
    /// back to the <see cref="AppRecord"/> objects in-memory, and streams progress to the terminal.
    /// Returns a (total, updates, errors) summary tuple.
    /// </summary>
    public async Task<(int Total, int Updates, int Errors)> CheckAsync(IReadOnlyList<AppRecord> apps, CancellationToken cancellationToken = default)
    {
        var appGroups = new List<(IScanner Scanner, AppRecord[] Apps)>();
        foreach (var scanner in scanners)
        {
            var scannerApps = apps.Where(c => c.App.Source.Name == scanner.Name);
            var allApps = scannerApps
                .Concat(scannerApps.Where(c => c.SubApps?.Any() == true).SelectMany(c => c.SubApps!))
                .ToDictionary(c => c);
            var groupedByScanner = allApps.Where(c => scanner.Kind.HasFlag(c.Value.App.Kind)).Select(c => c.Value).ToArray();
            if (groupedByScanner.Length == 0)
            {
                continue;
            }

            appGroups.Add((scanner, groupedByScanner));
        }

        var totalToCheck = appGroups.Sum(g => g.Apps.Length);
        renderer.SetCheckTotal(totalToCheck);

        // Periodic timer to refresh the check progress line with updated elapsed time
        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timerTask = renderer.RunCheckTimerAsync(timerCts.Token);

        int total = 0, errors = 0;
        var checkedApps = new List<AppRecord>();

        // Fan every scanner's CheckAsync out concurrently and merge the results through a single
        // bounded channel. A slow scanner (e.g. per-cask Homebrew lookups) no longer blocks the
        // others, and all mutation of the counters/list happens on the single reader below.
        await foreach (var (app, error) in appGroups.WhenAll<(IScanner Scanner, AppRecord[] Apps), (AppRecord App, bool Error)>(
                           onPublication: RunCheckGroupAsync,
                           cancellationToken: cancellationToken))
        {
            total++;

            if (error)
            {
                errors++;
            }

            app.CheckFailed = app.CheckFailed || error;
            checkedApps.Add(app);
            renderer.RenderCheckActive(total);
        }

        await timerCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await timerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        var updates = checkedApps.Count(c => c.HasUpdate);
        renderer.RenderCheckComplete(total, updates, errors);
        logger.LogInformation(
            "Check complete: {Total} checked, {Updates} updates, {Errors} errors",
            total, updates, errors);

        return (total, updates, errors);
    }

    private static async Task RunCheckGroupAsync(
        (IScanner Scanner, AppRecord[] Apps) group,
        ChannelWriter<(AppRecord App, bool Error)> writer,
        CancellationToken cancellationToken)
    {
        await foreach (var result in group.Scanner.CheckAsync(group.Apps, cancellationToken).ConfigureAwait(false))
        {
            await writer.WriteAsync(result, cancellationToken).ConfigureAwait(false);
        }
    }
}