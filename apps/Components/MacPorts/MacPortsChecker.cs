using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using apps.Infrastructure;
using apps.Checkers;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.MacPorts;

/// <summary>
/// Priority 7 checker — runs <c>port outdated</c> once and matches results by the
/// port name stored in <see cref="AppRecord.UpdateMethodDetail"/>.
/// Apps absent from the output are considered up to date.
/// </summary>
public sealed partial class MacPortsChecker(IProcessRunner runner, ILogger<MacPortsChecker> logger)
    : IUpdateChecker
{
    // "git        2.44.0_0 < 2.45.0_0" — captures port name and new version
    [GeneratedRegex(
        @"^(\S+)\s+\S+\s+<\s+(\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex OutdatedLineRegex();

    /// <inheritdoc/>
    public UpdateMethod Method => UpdateMethod.MacPorts;

    /// <inheritdoc/>
    public string DisplayName => "MacPorts";

    /// <inheritdoc/>
    public (string Label, string? Qualifier)? SourceOverride => ("MacPorts", null);

    /// <inheritdoc/>
    public bool CanCheck(AppRecord app) => app.UpdateMethod == UpdateMethod.MacPorts;

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
        var (outdated, error) = await RunPortOutdatedAsync(cancellationToken).ConfigureAwait(false);
        return apps.Select(app => BuildResult(app, outdated, error)).ToArray();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<UpdateCheckResult> CheckStreamAsync(
        IReadOnlyList<AppRecord> apps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (outdated, error) = await RunPortOutdatedAsync(cancellationToken).ConfigureAwait(false);

        foreach (var app in apps)
        {
            yield return BuildResult(app, outdated, error);
        }
    }

    private async Task<(Dictionary<string, string> Outdated, string? Error)> RunPortOutdatedAsync(CancellationToken cancellationToken)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        const string port = "/opt/local/bin/port";

        ProcessResult proc;
        try
        {
            proc = await runner.RunAsync(port, "outdated", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to run 'port outdated'");
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

            var name = m.Groups[1].Value;
            var raw_version = m.Groups[2].Value;

            // MacPorts versions include an epoch suffix (e.g. "2.45.0_0"); strip it.
            var underscoreIdx = raw_version.LastIndexOf('_');
            var latest = underscoreIdx > 0 ? raw_version[..underscoreIdx] : raw_version;

            lookup[name] = latest;
        }

        logger.LogDebug("port outdated: {Count} port(s) have updates", lookup.Count);
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
            return new UpdateCheckResult(app.Name, UpdateMethod.MacPorts, false, app.InstalledVersion, app.InstalledVersion);
        }

        return new UpdateCheckResult(app.Name, UpdateMethod.MacPorts, true, app.InstalledVersion, latest);
    }

    private static UpdateCheckResult Err(AppRecord app, string msg)
        => new(app.Name, UpdateMethod.MacPorts, false, app.InstalledVersion, null, msg);
}
