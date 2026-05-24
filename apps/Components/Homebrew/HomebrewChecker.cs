using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

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
/// </summary>
public sealed class HomebrewCaskChecker(IProcessRunner runner, ILogger<HomebrewCaskChecker> logger)
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

        var detail = app.UpdateMethodDetail ?? app.Name;

        // Apps matched via the Homebrew catalog but NOT installed through Homebrew carry a
        // "catalog:{token}:{latestVersion}" detail. The version is compared directly against
        // the installed version without relying on "brew outdated", which only covers
        // Homebrew-managed installations.
        if (detail.StartsWith("catalog:", StringComparison.Ordinal))
        {
            return BuildCatalogResult(app, detail);
        }

        if (!lookup.Casks.TryGetValue(detail, out var latest))
        {
            return new UpdateCheckResult(app.Name, UpdateMethod.HomebrewCask, false, app.InstalledVersion, app.InstalledVersion);
        }

        return new UpdateCheckResult(app.Name, UpdateMethod.HomebrewCask, true, app.InstalledVersion, latest);
    }

    /// <summary>
    /// Compares the installed version against the catalog version carried in <paramref name="detail"/>
    /// (format: <c>"catalog:{token}:{rawVersion}"</c>).
    /// The raw version may contain a comma-separated hash suffix (e.g. <c>"1.8555.2,abc123"</c>);
    /// only the part before the first comma is used for comparison.
    /// </summary>
    private static UpdateCheckResult BuildCatalogResult(AppRecord app, string detail)
    {
        // detail = "catalog:{token}:{rawVersion}"
        // Find the second colon (after "catalog:") to split off the version.
        var secondColon = detail.IndexOf(':', "catalog:".Length);

        if (secondColon < 0)
        {
            return new UpdateCheckResult(app.Name, UpdateMethod.HomebrewCask, false, app.InstalledVersion, app.InstalledVersion);
        }

        var rawVersion = detail[(secondColon + 1)..];

        // Strip any hash suffix that Homebrew appends after a comma.
        var commaIdx = rawVersion.IndexOf(',');
        var catalogVersion = (commaIdx >= 0 ? rawVersion[..commaIdx] : rawVersion).Trim();

        if (string.IsNullOrWhiteSpace(catalogVersion))
        {
            return new UpdateCheckResult(app.Name, UpdateMethod.HomebrewCask, false, app.InstalledVersion, app.InstalledVersion);
        }

        var updateAvailable = VersionComparer.IsNewer(app.InstalledVersion, catalogVersion);
        return new UpdateCheckResult(app.Name, UpdateMethod.HomebrewCask, updateAvailable, app.InstalledVersion, catalogVersion);
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


