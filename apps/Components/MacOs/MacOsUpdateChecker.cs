using System.Runtime.CompilerServices;

using apps.Infrastructure;
using apps.Checkers;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.MacOs;

/// <summary>
/// Specialised checker — re-runs <c>softwareupdate --list --all</c> and confirms
/// which pending macOS system updates discovered by <see cref="MacOsUpdateScanner"/>
/// are still available.
///
/// Handles apps from the <c>macOS</c> scanner. Since <c>softwareupdate --list</c>
/// only emits items that have pending updates, any app label that appears in the
/// output is definitively outdated; absence means it has been applied.
/// <see cref="AppRecord.UpdateMethodDetail"/> holds the available version string
/// parsed during scanning.
/// </summary>
public sealed class MacOsUpdateChecker(IProcessRunner runner, ILogger<MacOsUpdateChecker> logger)
    : IUpdateChecker
{
    /// <inheritdoc/>
    public UpdateMethod Method => UpdateMethod.Specialised;

    /// <inheritdoc/>
    public string DisplayName => "macOS Software Update";

    /// <inheritdoc/>
    public bool CanCheck(AppRecord app)
        => app.UpdateMethod == UpdateMethod.Specialised
           && string.Equals(app.Identifier.Name, "macOS", StringComparison.Ordinal);

    /// <inheritdoc/>
    public async Task<UpdateCheckResult> CheckAsync(AppRecord app, CancellationToken cancellationToken = default)
    {
        var results = await CheckBatchAsync([app], cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<UpdateCheckResult>> CheckBatchAsync(
        IReadOnlyList<AppRecord> apps,
        CancellationToken cancellationToken = default)
    {
        var pending = await GetPendingLabelsAsync(cancellationToken).ConfigureAwait(false);
        return apps.Select(app => BuildResult(app, pending)).ToArray();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<UpdateCheckResult> CheckStreamAsync(
        IReadOnlyList<AppRecord> apps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pending = await GetPendingLabelsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var app in apps)
        {
            yield return BuildResult(app, pending);
        }
    }

    private async Task<HashSet<string>> GetPendingLabelsAsync(CancellationToken cancellationToken)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ProcessResult proc;
        try
        {
            proc = await runner.RunAsync("/usr/sbin/softwareupdate", "--list --all", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to run 'softwareupdate --list --all'");
            return labels;
        }

        // softwareupdate exits 1 when there are no updates — not a real error.
        var output = proc.StandardOutput + proc.StandardError;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimStart();

            if (!line.StartsWith("* Label:", StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith("** Label:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var labelIdx = line.IndexOf("Label:", StringComparison.OrdinalIgnoreCase);
            var label = line[(labelIdx + "Label:".Length)..].Trim();

            if (!string.IsNullOrEmpty(label))
            {
                labels.Add(label);
            }
        }

        logger.LogDebug("softwareupdate: {Count} pending update(s) found", labels.Count);
        return labels;
    }

    private static UpdateCheckResult BuildResult(AppRecord app, HashSet<string> pendingLabels)
    {
        var latestVersion = app.UpdateMethodDetail; // version parsed during scan
        var stillPending = pendingLabels.Contains(app.Name);

        return new UpdateCheckResult(
            app.Name,
            UpdateMethod.Specialised,
            stillPending,
            app.InstalledVersion,
            stillPending ? latestVersion : app.InstalledVersion);
    }
}

