using System.Threading.Channels;

using apps.Checkers;
using apps.Infrastructure;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Orchestration;

/// <summary>
/// Stage 2 of the pipeline: groups apps by their resolved UpdateMethod,
/// starts all checker groups concurrently, and streams results through a
/// bounded channel. Each result is applied back to its <see cref="AppRecord"/>
/// in-memory so the caller can inspect update status after this stage.
///
/// Results print to the terminal as each check completes — no waiting for
/// the full batch.
/// </summary>
public sealed class CheckOrchestrator(
    IEnumerable<IUpdateChecker> checkers,
    LiveProgressRenderer renderer,
    ILogger<CheckOrchestrator> logger)
{
    private const int ResultChannelCapacity = 256;

    /// <summary>
    /// Groups apps by update method, fans out all checkers concurrently, applies results
    /// back to the <see cref="AppRecord"/> objects in-memory, and streams progress to the terminal.
    /// Returns a (total, updates, errors) summary tuple.
    /// </summary>
    public async Task<(int Total, int Updates, int Errors)> RunAsync(
        IReadOnlyList<AppRecord> apps,
        CancellationToken cancellationToken = default)
    {
        var checkersByMethod = checkers
            .GroupBy(c => c.Method)
            .ToDictionary(g => g.Key, g => g.ToList());

        var grouped = apps
            .Where(a => a.UpdateMethod.HasValue && !a.IsPinned)
            .GroupBy(a => a.UpdateMethod!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AppRecord>)[..g]);

        if (grouped.Count == 0)
        {
            logger.LogInformation("No apps with resolved update methods found");
            return (0, 0, 0);
        }

        // Build a lookup so results can be applied back to ALL records with the same name.
        var recordsByName = apps
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);

        var resultChannel = Channel.CreateBounded<UpdateCheckResult>(
            new BoundedChannelOptions(ResultChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

        var checkGroups = grouped
            .Where(kv => checkersByMethod.ContainsKey(kv.Key))
            .SelectMany(kv => checkersByMethod[kv.Key].Select(checker =>
            {
                var eligible = kv.Value
                    .Where(a => checker.CanCheck(a))
                    .ToList();

                return (eligible, checker, method: kv.Key);
            }))
            .ToList();

        var totalToCheck = checkGroups.Sum(g => g.eligible.Count);
        renderer.SetCheckTotal(totalToCheck);

        // Periodic timer to refresh the check progress line with updated elapsed time
        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timerTask = renderer.RunCheckTimerAsync(timerCts.Token);

        var checkTasks = checkGroups
            .Select(g => CheckGroupAsync(g.method, g.eligible, g.checker, resultChannel.Writer, cancellationToken))
            .ToList();

        _ = Task.WhenAll(checkTasks)
            .ContinueWith(t =>
            {
                _ = t.Exception;
                resultChannel.Writer.TryComplete();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        int total = 0, updates = 0, errors = 0;

        await foreach (var result in resultChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            total++;

            if (result.UpdateAvailable)
            {
                updates++;
            }

            if (result.Error is not null)
            {
                errors++;
            }

            if (recordsByName.TryGetValue(result.AppName, out var records))
            {
                foreach (var record in records)
                {
                    record.UpdateAvailable = result.UpdateAvailable;
                    record.LatestVersion = result.LatestVersion;
                    record.LastCheckError = result.Error;

                    if (result.InstalledVersion is not null)
                    {
                        record.InstalledVersion = result.InstalledVersion;
                    }
                }
            }

            renderer.RenderCheckActive(total);
        }

        await timerCts.CancelAsync().ConfigureAwait(false);
        try { await timerTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

        renderer.RenderCheckComplete(total, updates, errors);
        logger.LogInformation(
            "Check complete: {Total} checked, {Updates} updates, {Errors} errors",
            total, updates, errors);

        return (total, updates, errors);
    }

    private async Task CheckGroupAsync(
        UpdateMethod method,
        List<AppRecord> apps,
        IUpdateChecker checker,
        ChannelWriter<UpdateCheckResult> writer,
        CancellationToken cancellationToken)
    {
        if (apps.Count == 0)
        {
            return;
        }

        logger.LogDebug("Checking {Count} apps via {Method}", apps.Count, method);

        try
        {
            await foreach (var result in checker.CheckStreamAsync(apps, cancellationToken).ConfigureAwait(false))
            {
                await writer.WriteAsync(result, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Checker {Method} failed", method);

            foreach (var app in apps)
            {
                var errResult = new UpdateCheckResult(
                    app.Name, method,
                    false,
                    app.InstalledVersion,
                    null,
                    ex.Message);

                await writer.WriteAsync(errResult, cancellationToken);
            }
        }
    }
}