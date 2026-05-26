using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

using apps.Infrastructure;
using apps.Checkers;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Dotnet;

/// <summary>
/// Priority 8 checker — queries the NuGet flat-container API for the latest
/// non-prerelease version of each package.
///
/// Handles apps from the following scanners: <c>NuGet</c> (global tools),
/// <c>NugetLocalTools</c> (manifest-pinned tools), and <c>NugetProject</c>
/// (project references — opt-in).
///
/// Uses <c>GET https://api.nuget.org/v3-flatcontainer/{id}/index.json</c>
/// which returns all published versions and is served from Fastly CDN (~200 req/min safe).
/// All checks fan out concurrently via the shared <c>"nuget"</c> named client.
/// In-flight request coalescing ensures each package ID is fetched at most once per run.
/// </summary>
public sealed class NugetRegistryChecker(IHttpClientFactory httpClientFactory, ILogger<NugetRegistryChecker> logger)
    : IUpdateChecker
{
    private static readonly HashSet<string> NugetScanners = new(StringComparer.OrdinalIgnoreCase)
    {
        "NuGet", "NugetLocalTools", "NugetProject"
    };

    private readonly ConcurrentDictionary<string, Task<string?>> _inflightLatest = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public UpdateMethod Method => UpdateMethod.PackageRegistry;

    /// <inheritdoc/>
    public string DisplayName => "NuGet";

    /// <inheritdoc/>
    public bool CanCheck(AppRecord app)
        => app.UpdateMethod == UpdateMethod.PackageRegistry
           && NugetScanners.Contains(app.Identifier.Name);

    /// <inheritdoc/>
    public async Task<UpdateCheckResult> CheckAsync(AppRecord app, CancellationToken cancellationToken = default)
        => await CheckOneAsync(app, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<UpdateCheckResult>> CheckBatchAsync(
        IReadOnlyList<AppRecord> apps,
        CancellationToken cancellationToken = default)
    {
        var tasks = apps.Select(a => CheckOneAsync(a, cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<UpdateCheckResult> CheckStreamAsync(
        IReadOnlyList<AppRecord> apps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var task in Task.WhenEach(apps.Select(a => CheckOneAsync(a, cancellationToken))).ConfigureAwait(false))
        {
            yield return await task.ConfigureAwait(false);
        }
    }

    private async Task<UpdateCheckResult> CheckOneAsync(AppRecord app, CancellationToken cancellationToken)
    {
        var packageId = (app.UpdateMethodDetail ?? app.Name).ToLowerInvariant();

        try
        {
            var latest = await _inflightLatest.GetOrAdd(packageId, id => FetchLatestVersionAsync(id, cancellationToken)).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(latest))
            {
                return Err(app, "No versions found in NuGet registry");
            }

            var updateAvailable = VersionComparer.IsNewer(app.InstalledVersion, latest);
            return new UpdateCheckResult(app.Name, UpdateMethod.PackageRegistry, updateAvailable, app.InstalledVersion, latest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "NuGet registry check failed for {Name} ({Id})",
                app.Name,
                packageId);
            return Err(app, ex.Message);
        }
    }

    private async Task<string?> FetchLatestVersionAsync(string packageId, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("nuget");
        var index = await client
            .GetFromJsonAsync(
                $"/v3-flatcontainer/{packageId}/index.json",
                DotnetJsonContext.Default.NugetVersionIndex,
                cancellationToken)
            .ConfigureAwait(false);

        var latest = index?.Versions?
            .Where(v => !v.Contains('-', StringComparison.Ordinal))
            .LastOrDefault();

        if (string.IsNullOrWhiteSpace(latest))
        {
            latest = index?.Versions?.LastOrDefault();
        }

        return latest;
    }

    private static UpdateCheckResult Err(AppRecord app, string msg)
        => new(app.Name, UpdateMethod.PackageRegistry, false, app.InstalledVersion, null, msg);
}


