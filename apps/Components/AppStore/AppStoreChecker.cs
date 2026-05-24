using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using apps.Infrastructure;
using apps.Checkers;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.AppStore;

/// <summary>
/// Priority 1 checker — App Store updates.
///
/// Strategy (two-pass):
///   1. Run <c>mas outdated</c> exactly once for the batch.
///      Any app that appears in the output is immediately resolved with its
///      installed → latest versions.
///   2. All remaining apps (not listed by <c>mas</c>, or when <c>mas</c> is absent / broken)
///      are checked against the iTunes Store Lookup API
///      (<c>https://itunes.apple.com/lookup</c>).
///      Lookup falls back from Apple ID (<see cref="AppRecord.UpdateMethodDetail"/>) to
///      bundle ID (<see cref="AppRecord.BundleId"/>), so it works even when
///      <c>mas list</c> produced no output.
///
/// All iTunes queries fan out concurrently; results stream as each response arrives.
/// </summary>
public sealed partial class AppStoreChecker(
    IProcessRunner runner,
    IHttpClientFactory httpClientFactory,
    ILogger<AppStoreChecker> logger)
    : IUpdateChecker
{
    // "1333542190 1Password (8.10.36 -> 8.10.40)"
    [GeneratedRegex(
        pattern: @"^(\d+)\s+(.+?)\s+\(([^\s]+)\s+->\s+([^\s)]+)\)",
        options: RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex OutdatedLineRegex();

    private static readonly string[] MasCandidates =
    [
        "/opt/homebrew/bin/mas",
        "/usr/local/bin/mas"
    ];

    /// <inheritdoc/>
    public UpdateMethod Method => UpdateMethod.AppStore;

    /// <inheritdoc/>
    public string DisplayName => "App Store";

    /// <inheritdoc/>
    public (string Label, string? Qualifier)? SourceOverride => ("App Store", null);

    /// <inheritdoc/>
    public bool CanCheck(AppRecord app) => app.UpdateMethod == UpdateMethod.AppStore;

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
        var results = new List<UpdateCheckResult>(apps.Count);
        await foreach (var r in CheckStreamAsync(apps, cancellationToken).ConfigureAwait(false))
        {
            results.Add(r);
        }

        return results;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Pass 1: resolves apps via <c>mas outdated</c> instantly (no per-app I/O).
    /// Pass 2: fans out iTunes API calls for every app not resolved in pass 1.
    /// </remarks>
    public async IAsyncEnumerable<UpdateCheckResult> CheckStreamAsync(
        IReadOnlyList<AppRecord> apps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (outdated, masError) = await RunMasOutdatedAsync(cancellationToken).ConfigureAwait(false);

        var needsItunes = new List<AppRecord>(apps.Count);

        foreach (var app in apps)
        {
            if (TryBuildMasResult(app, outdated, masError) is { } masResult)
            {
                yield return masResult;
            }
            else
            {
                needsItunes.Add(app);
            }
        }

        if (needsItunes.Count == 0)
        {
            yield break;
        }

        await foreach (var task in Task.WhenEach(needsItunes.Select(a => CheckWithItunesAsync(a, cancellationToken))).ConfigureAwait(false))
        {
            yield return await task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns a result when <c>mas outdated</c> definitively resolves the app
    /// (the app appeared in the <c>mas outdated</c> output, or <c>mas</c> returned a hard error).
    /// Returns <see langword="null"/> when the app was absent from the output — which simply
    /// means mas didn't report it, not necessarily that it is up to date.
    /// </summary>
    private static UpdateCheckResult? TryBuildMasResult(
        AppRecord app,
        Dictionary<string, (string Installed, string Latest)> outdated,
        string? masError)
    {
        if (masError is not null)
        {
            return new UpdateCheckResult(app.Name, UpdateMethod.AppStore, false, app.InstalledVersion, null, masError);
        }

        // Match by Apple ID first, then by display name.
        var matched = (app.UpdateMethodDetail is not null && outdated.TryGetValue(app.UpdateMethodDetail, out var info)) ||
                      outdated.TryGetValue(app.Name, out info);

        if (!matched)
        {
            return null;
        }

        return new UpdateCheckResult(app.Name, UpdateMethod.AppStore, true, info.Installed, info.Latest);
    }

    /// <summary>
    /// Queries the iTunes Store Lookup API for the current App Store version.
    /// Prefers lookup by Apple ID; falls back to bundle ID.
    /// </summary>
    private async Task<UpdateCheckResult> CheckWithItunesAsync(AppRecord app, CancellationToken cancellationToken)
    {
        string? query = null;

        if (app.UpdateMethodDetail is { Length: > 0 } appleId && long.TryParse(appleId, out _))
        {
            query = $"/lookup?id={appleId}";
        }
        else if (app.BundleId is { Length: > 0 } bundleId)
        {
            query = $"/lookup?bundleId={bundleId}";
        }

        if (query is null)
        {
            return new UpdateCheckResult(app.Name, UpdateMethod.AppStore, false, app.InstalledVersion, app.InstalledVersion);
        }

        try
        {
            using var client = httpClientFactory.CreateClient("itunes");
            var response = await client
                .GetFromJsonAsync(query, AppStoreJsonContext.Default.ItunesLookupResponse, cancellationToken)
                .ConfigureAwait(false);

            var result = response?.Results?.FirstOrDefault(r =>
                string.Equals(r.Kind, "mac-software", StringComparison.OrdinalIgnoreCase));

            if (result?.Version is not { Length: > 0 } latestVersion)
            {
                logger.LogDebug(
                    "iTunes lookup for {App}: no mac-software result found (response may contain iOS-only records)",
                    app.Name);
                return new UpdateCheckResult(app.Name, UpdateMethod.AppStore, false, app.InstalledVersion, app.InstalledVersion);
            }

            var updateAvailable = VersionComparer.IsNewer(app.InstalledVersion, latestVersion);
            return new UpdateCheckResult(app.Name, UpdateMethod.AppStore, updateAvailable, app.InstalledVersion, latestVersion);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "iTunes lookup failed for {App}: {Message}",
                app.Name,
                ex.Message);
            logger.LogDebug(ex,
                "iTunes lookup exception detail for {App}",
                app.Name);
            return Err(app, ex.Message);
        }
    }

    /// <summary>
    /// Runs <c>mas outdated</c> and parses its output into a lookup.
    /// Keys are Apple IDs and display names; values are (installed, latest) version pairs.
    /// Returns an empty dict when <c>mas</c> is absent or produces no output — callers
    /// must not treat an empty result as "all apps up to date".
    /// </summary>
    private async Task<(Dictionary<string, (string Installed, string Latest)> Outdated, string? Error)> RunMasOutdatedAsync(CancellationToken cancellationToken)
    {
        var lookup = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        var mas = MasCandidates.FirstOrDefault(File.Exists);

        if (mas is null)
        {
            logger.LogDebug("mas CLI not found; skipping mas pass — iTunes API will be used instead");
            return (lookup, null);
        }

        ProcessResult proc;
        try
        {
            proc = await runner.RunAsync(mas, "outdated", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Hard failure running mas — propagate as error so callers skip iTunes too.
            logger.LogWarning("Failed to run 'mas outdated': {Message}", ex.Message);
            logger.LogDebug(ex, "mas outdated exception detail");
            return (lookup, $"Failed to run mas: {ex.Message}");
        }

        if (!proc.Success && !string.IsNullOrWhiteSpace(proc.StandardError))
        {
            var err = proc.StandardError.Trim();
            logger.LogWarning("'mas outdated' exited {Code}: {Error}", proc.ExitCode, err);
            return (lookup, err);
        }

        foreach (var raw in proc.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            var m = OutdatedLineRegex().Match(line);
            if (!m.Success)
            {
                logger.LogDebug("Skipping unrecognised mas line: {Line}", line);
                continue;
            }

            var appleId = m.Groups[1].Value;
            var name = m.Groups[2].Value.Trim();
            var installed = m.Groups[3].Value;
            var latest = m.Groups[4].Value;

            lookup[appleId] = (installed, latest);
            lookup[name] = (installed, latest);
        }

        if (lookup.Count > 0)
        {
            logger.LogDebug("mas outdated: {Count} app(s) have updates", lookup.Count / 2);
        }

        return (lookup, null);
    }

    private static UpdateCheckResult Err(AppRecord app, string msg)
        => new(app.Name, UpdateMethod.AppStore, false, app.InstalledVersion, null, msg);
}

internal sealed class ItunesLookupResponse
{
    [JsonPropertyName("resultCount")]
    public int ResultCount { get; init; }

    [JsonPropertyName("results")]
    public ItunesResult[]? Results { get; init; }
}

internal sealed class ItunesResult
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("trackName")]
    public string? TrackName { get; init; }

    [JsonPropertyName("bundleId")]
    public string? BundleId { get; init; }

    /// <summary>
    /// Platform discriminator: <c>"mac-software"</c> for native macOS apps,
    /// <c>"software"</c> for iOS apps. Universal apps that share a bundle ID
    /// often return the iOS record whose version may differ from the macOS build.
    /// </summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }
}

[JsonSerializable(typeof(ItunesLookupResponse))]
internal sealed partial class AppStoreJsonContext : JsonSerializerContext;

