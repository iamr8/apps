using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

using apps.Infrastructure;
using apps.Checkers;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Homebrew;

/// <summary>
/// Priority 3 checker — handles Homebrew formula updates.
/// Runs <c>brew outdated --json=v2</c> once per batch and matches results by
/// the formula name stored in <see cref="AppRecord.UpdateMethodDetail"/>.
/// </summary>
public sealed class HomebrewFormulaChecker(IProcessRunner runner, ILogger<HomebrewFormulaChecker> logger)
    : IUpdateChecker
{
    /// <inheritdoc/>
    public UpdateMethod Method => UpdateMethod.HomebrewFormula;

    /// <inheritdoc/>
    public string DisplayName => "Homebrew Formula";

    /// <inheritdoc/>
    public (string Label, string? Qualifier)? SourceOverride => ("Homebrew", "Formula");

    /// <inheritdoc/>
    public bool CanCheck(AppRecord app) => app.UpdateMethod == UpdateMethod.HomebrewFormula;

    /// <inheritdoc/>
    public async Task<UpdateCheckResult> CheckAsync(AppRecord app, CancellationToken cancellationToken = default)
    {
        var results = await CheckBatchAsync([app], cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<UpdateCheckResult>> CheckBatchAsync(IReadOnlyList<AppRecord> apps, CancellationToken cancellationToken = default)
    {
        var (lookup, error) = await HomebrewOutdated.RunAsync(runner, logger, cancellationToken).ConfigureAwait(false);
        return apps.Select(app => BuildResult(app, lookup, error)).ToArray();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<UpdateCheckResult> CheckStreamAsync(
        IReadOnlyList<AppRecord> apps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (lookup, error) = await HomebrewOutdated.RunAsync(runner, logger, cancellationToken).ConfigureAwait(false);

        foreach (var app in apps)
        {
            yield return BuildResult(app, lookup, error);
        }
    }

    private static UpdateCheckResult BuildResult(AppRecord app, HomebrewOutdated.Lookup lookup, string? error)
    {
        if (error is not null)
        {
            return Err(app, error);
        }

        var key = app.UpdateMethodDetail ?? app.Name;

        if (!lookup.Formulae.TryGetValue(key, out var latest))
        {
            return new UpdateCheckResult(app.Name, UpdateMethod.HomebrewFormula, false, app.InstalledVersion, app.InstalledVersion);
        }

        return new UpdateCheckResult(app.Name, UpdateMethod.HomebrewFormula, true, app.InstalledVersion, latest);
    }

    private static UpdateCheckResult Err(AppRecord app, string msg)
        => new(app.Name, UpdateMethod.HomebrewFormula, false, app.InstalledVersion, null, msg);
}

/// <summary>
/// Priority 2 checker — handles Homebrew cask updates.
/// Reuses the same <c>brew outdated --json=v2</c> subprocess result as
/// <see cref="HomebrewFormulaChecker"/> via a short-lived static cache,
/// avoiding a duplicate subprocess when both checkers run concurrently.
///
/// For apps matched via the Homebrew catalog (not installed through Homebrew),
/// fetches the latest version from <c>https://formulae.brew.sh/api/cask/{token}.json</c>
/// to avoid relying on potentially stale local Homebrew cache data.
/// </summary>
public sealed class HomebrewCaskChecker(
    IProcessRunner runner,
    IHttpClientFactory httpClientFactory,
    ILogger<HomebrewCaskChecker> logger)
    : IUpdateChecker
{
    /// <inheritdoc/>
    public UpdateMethod Method => UpdateMethod.HomebrewCask;

    /// <inheritdoc/>
    public string DisplayName => "Homebrew Cask";

    /// <inheritdoc/>
    public (string Label, string? Qualifier)? SourceOverride => ("Homebrew", "Cask");

    /// <inheritdoc/>
    public bool CanCheck(AppRecord app) => app.UpdateMethod == UpdateMethod.HomebrewCask;

    /// <inheritdoc/>
    public async Task<UpdateCheckResult> CheckAsync(AppRecord app, CancellationToken cancellationToken = default)
    {
        var results = await CheckBatchAsync([app], cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<UpdateCheckResult>> CheckBatchAsync(IReadOnlyList<AppRecord> apps, CancellationToken cancellationToken = default)
    {
        var (lookup, error) = await HomebrewOutdated.RunAsync(runner, logger, cancellationToken).ConfigureAwait(false);
        var tasks = apps.Select(app => BuildResultAsync(app, lookup, error, cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<UpdateCheckResult> CheckStreamAsync(
        IReadOnlyList<AppRecord> apps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (lookup, error) = await HomebrewOutdated.RunAsync(runner, logger, cancellationToken).ConfigureAwait(false);

        foreach (var app in apps)
        {
            yield return await BuildResultAsync(app, lookup, error, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<UpdateCheckResult> BuildResultAsync(AppRecord app, HomebrewOutdated.Lookup lookup, string? error, CancellationToken cancellationToken)
    {
        if (error is not null)
        {
            return Err(app, error);
        }

        var detail = app.UpdateMethodDetail ?? app.Name;

        if (detail.StartsWith("catalog:", StringComparison.Ordinal))
        {
            return await BuildCatalogResultAsync(app, detail, cancellationToken).ConfigureAwait(false);
        }

        if (!lookup.Casks.TryGetValue(detail, out var latest))
        {
            return new UpdateCheckResult(app.Name, UpdateMethod.HomebrewCask, false, app.InstalledVersion, app.InstalledVersion);
        }

        return new UpdateCheckResult(app.Name, UpdateMethod.HomebrewCask, true, app.InstalledVersion, latest);
    }

    /// <summary>
    /// Fetches the latest cask version from the Homebrew Formulae API for catalog-matched apps.
    /// Falls back to the version embedded in <paramref name="detail"/> if the API call fails.
    /// </summary>
    private async Task<UpdateCheckResult> BuildCatalogResultAsync(AppRecord app, string detail, CancellationToken cancellationToken)
    {
        var secondColon = detail.IndexOf(':', "catalog:".Length);

        if (secondColon < 0)
        {
            return new UpdateCheckResult(app.Name, UpdateMethod.HomebrewCask, false, app.InstalledVersion, app.InstalledVersion);
        }

        var token = detail["catalog:".Length..secondColon];
        var fallbackVersion = detail[(secondColon + 1)..];

        var catalogVersion = await FetchCaskVersionAsync(token, cancellationToken).ConfigureAwait(false) ?? fallbackVersion;

        var commaIdx = catalogVersion.IndexOf(',');
        var cleanVersion = (commaIdx >= 0 ? catalogVersion[..commaIdx] : catalogVersion).Trim();

        if (string.IsNullOrWhiteSpace(cleanVersion))
        {
            return new UpdateCheckResult(app.Name, UpdateMethod.HomebrewCask, false, app.InstalledVersion, app.InstalledVersion);
        }

        var updateAvailable = VersionComparer.IsNewer(app.InstalledVersion, cleanVersion);
        return new UpdateCheckResult(app.Name, UpdateMethod.HomebrewCask, updateAvailable, app.InstalledVersion, cleanVersion);
    }

    /// <summary>
    /// Queries <c>https://formulae.brew.sh/api/cask/{token}.json</c> for the latest version.
    /// Returns <c>null</c> on any failure (network, 404, parse error).
    /// </summary>
    private async Task<string?> FetchCaskVersionAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("homebrew-api");
            var response = await client
                .GetFromJsonAsync($"/api/cask/{token}.json", HomebrewJsonContext.Default.BrewCaskApiResponse, cancellationToken)
                .ConfigureAwait(false);

            return response?.Version;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to fetch cask version from Homebrew API for '{Token}'", token);
            return null;
        }
    }

    private static UpdateCheckResult Err(AppRecord app, string msg)
        => new(app.Name, UpdateMethod.HomebrewCask, false, app.InstalledVersion, null, msg);
}

/// <summary>
/// Shared helper that runs <c>brew outdated --json=v2</c> and parses both
/// the formulae and casks sections into keyed lookup dictionaries.
/// A 60-second static cache prevents duplicate subprocess invocations when
/// <see cref="HomebrewFormulaChecker"/> and <see cref="HomebrewCaskChecker"/>
/// are dispatched concurrently for the same check run.
/// </summary>
internal static class HomebrewOutdated
{
    internal sealed record Lookup(
        Dictionary<string, string> Formulae,
        Dictionary<string, string> Casks);

    private static readonly string[] BrewCandidates = ["/opt/homebrew/bin/brew", "/usr/local/bin/brew"];
    private static readonly Lock CacheLock = new();
    private static Task<(Lookup, string?)>? _cachedTask;
    private static DateTimeOffset _cacheExpiry;

    /// <summary>
    /// Returns a cached task for the brew outdated result, starting a new subprocess
    /// only when the cache has expired (60-second TTL).
    /// </summary>
    internal static Task<(Lookup, string?)> RunAsync(IProcessRunner runner, ILogger logger, CancellationToken cancellationToken)
    {
        lock (CacheLock)
        {
            if (_cachedTask is not null && DateTimeOffset.UtcNow < _cacheExpiry)
            {
                return _cachedTask;
            }

            _cachedTask = RunUncachedAsync(runner, logger, cancellationToken);
            _cacheExpiry = DateTimeOffset.UtcNow.AddMinutes(1);
            return _cachedTask;
        }
    }

    private static async Task<(Lookup, string?)> RunUncachedAsync(IProcessRunner runner, ILogger logger, CancellationToken cancellationToken)
    {
        var empty = new Lookup(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
        var brew = BrewCandidates.FirstOrDefault(File.Exists);

        if (brew is null)
        {
            return (empty, "brew not found");
        }

        ProcessResult proc;
        try
        {
            proc = await runner.RunAsync(brew, "outdated --json=v2", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to run 'brew outdated --json=v2'");
            return (empty, ex.Message);
        }

        if (!proc.Success && string.IsNullOrWhiteSpace(proc.StandardOutput))
        {
            var err = proc.StandardError.Trim();
            logger.LogWarning("'brew outdated --json=v2' failed: {Error}", err);
            return (empty, err);
        }

        try
        {
            var root = JsonSerializer.Deserialize(proc.StandardOutput, HomebrewJsonContext.Default.BrewOutdatedRoot);
            var formulae = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var casks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (root?.Formulae is not null)
            {
                foreach (var f in root.Formulae)
                {
                    if (!string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.CurrentVersion))
                    {
                        formulae[f.Name] = f.CurrentVersion;
                    }
                }
            }

            if (root?.Casks is not null)
            {
                foreach (var c in root.Casks)
                {
                    if (!string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.CurrentVersion))
                    {
                        casks[c.Name] = c.CurrentVersion;
                    }
                }
            }

            logger.LogDebug(
                "brew outdated: {FormulaCount} formula(e), {CaskCount} cask(s) have updates",
                formulae.Count,
                casks.Count);

            return (new Lookup(formulae, casks), null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse 'brew outdated --json=v2' output");
            return (empty, ex.Message);
        }
    }
}


