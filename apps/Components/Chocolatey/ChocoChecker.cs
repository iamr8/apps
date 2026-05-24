using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using apps.Infrastructure;
using apps.Checkers;
using apps.Models;
using apps.Scanners;

using Microsoft.Extensions.Logging;

namespace apps.Components.Chocolatey;

/// <summary>
/// Priority 8 checker — runs <c>choco outdated</c> once and matches results by the
/// package name stored in <see cref="AppRecord.UpdateMethodDetail"/>.
/// Output format: <c>packagename|current|latest|pinned</c>.
/// Apps absent from the output are considered up to date.
/// Gracefully returns an error result if <c>choco</c> is not found.
/// </summary>
public sealed partial class ChocoChecker(IProcessRunner runner, ILogger<ChocoChecker> logger)
    : IUpdateChecker
{
    // "git|2.40.1|2.43.0|false" — captures name (group 1) and latest version (group 3)
    [GeneratedRegex(
        @"^([^|]+)\|([^|]+)\|([^|]+)\|",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex OutdatedLineRegex();

    /// <inheritdoc/>
    public UpdateMethod Method => UpdateMethod.Chocolatey;

    /// <inheritdoc/>
    public string DisplayName => "Chocolatey";

    /// <inheritdoc/>
    public (string Label, string? Qualifier)? SourceOverride => ("Chocolatey", null);

    /// <inheritdoc/>
    public bool CanCheck(AppRecord app) => app.UpdateMethod == UpdateMethod.Chocolatey;

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
        var (outdated, error) = await RunChocoOutdatedAsync(cancellationToken).ConfigureAwait(false);
        return apps.Select(app => BuildResult(app, outdated, error)).ToArray();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<UpdateCheckResult> CheckStreamAsync(
        IReadOnlyList<AppRecord> apps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (outdated, error) = await RunChocoOutdatedAsync(cancellationToken).ConfigureAwait(false);

        foreach (var app in apps)
        {
            yield return BuildResult(app, outdated, error);
        }
    }

    private async Task<(Dictionary<string, string> Outdated, string? Error)> RunChocoOutdatedAsync(CancellationToken cancellationToken)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var choco = ScannerHelper.FindExecutable("choco");

        if (choco is null)
        {
            logger.LogDebug("choco not found — skipping Chocolatey check");
            return (lookup, "choco not found");
        }

        ProcessResult proc;
        try
        {
            // --yes suppresses interactive confirmation prompts without performing any install
            proc = await runner.RunAsync(choco, "outdated --yes", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to run 'choco outdated'");
            return (lookup, ex.Message);
        }

        foreach (var raw in proc.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var m = OutdatedLineRegex().Match(line);

            if (!m.Success)
            {
                continue;
            }

            var name = m.Groups[1].Value.Trim();
            var latest = m.Groups[3].Value.Trim();

            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(latest))
            {
                lookup[name] = latest;
            }
        }

        logger.LogDebug("choco outdated: {Count} package(s) have updates", lookup.Count);
        return (lookup, null);
    }

    private static UpdateCheckResult BuildResult(
        AppRecord app,
        Dictionary<string, string> outdated,
        string? error)
    {
        if (error is not null)
        {
            return Err(app, error);
        }

        var key = app.UpdateMethodDetail ?? app.Name;

        if (!outdated.TryGetValue(key, out var latest))
        {
            return new UpdateCheckResult(app.Name, UpdateMethod.Chocolatey, false, app.InstalledVersion, app.InstalledVersion);
        }

        return new UpdateCheckResult(app.Name, UpdateMethod.Chocolatey, true, app.InstalledVersion, latest);
    }

    private static UpdateCheckResult Err(AppRecord app, string msg)
        => new(app.Name, UpdateMethod.Chocolatey, false, app.InstalledVersion, null, msg);
}

