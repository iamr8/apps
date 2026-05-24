using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

using apps.Infrastructure;
using apps.Checkers;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Go;

/// <summary>
/// Priority 8 checker — queries the Go module proxy for the latest version of each module.
///
/// Handles apps from the following scanners: <c>GoTools</c> (GOPATH/bin binaries with module info)
/// and <c>GoMod</c> (project dependencies — opt-in).
///
/// Uses <c>GET https://proxy.golang.org/{module}/@latest</c> which is served from
/// Google's CDN and is effectively unlimited. The <see cref="AppRecord.UpdateMethodDetail"/>
/// must contain the module path (e.g. <c>github.com/user/repo/cmd/tool</c>).
/// All checks fan out concurrently via the shared <c>"goproxy"</c> named client.
/// </summary>
public sealed class GoModProxyChecker(IHttpClientFactory httpClientFactory, ILogger<GoModProxyChecker> logger)
    : IUpdateChecker
{
    private static readonly HashSet<string> GoScanners = new(StringComparer.OrdinalIgnoreCase)
    {
        "GoTools", "GoMod"
    };

    /// <inheritdoc/>
    public UpdateMethod Method => UpdateMethod.PackageRegistry;

    /// <inheritdoc/>
    public string DisplayName => "Go Module Proxy";

    /// <inheritdoc/>
    public bool CanCheck(AppRecord app)
        => app.UpdateMethod == UpdateMethod.PackageRegistry
           && GoScanners.Contains(app.Scanner)
           && app.UpdateMethodDetail is not null;

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
        var modulePath = app.UpdateMethodDetail!;

        try
        {
            using var client = httpClientFactory.CreateClient("goproxy");
            var info = await FetchModuleLatestAsync(client, modulePath, cancellationToken).ConfigureAwait(false);

            if (info is null)
            {
                // Module root not found in proxy — binary may be a cmd/ subpackage of an
                // unpublished or private module; skip silently.
                logger.LogDebug(
                    "Go module proxy: no module found for {Name} ({Module}); skipping",
                    app.Name,
                    modulePath);
                return new UpdateCheckResult(app.Name, UpdateMethod.PackageRegistry, false, app.InstalledVersion, app.InstalledVersion);
            }

            // Go versions include the leading "v" prefix (e.g. "v1.23.4"); strip it for comparison.
            var latest = info.Version?.TrimStart('v');

            if (string.IsNullOrWhiteSpace(latest))
            {
                return Err(app, "Go module proxy returned no version");
            }

            var updateAvailable = VersionComparer.IsNewer(app.InstalledVersion, latest);
            return new UpdateCheckResult(app.Name, UpdateMethod.PackageRegistry, updateAvailable, app.InstalledVersion, latest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Go module proxy check failed for {Name} ({Module})",
                app.Name,
                modulePath);
            return Err(app, ex.Message);
        }
    }

    /// <summary>
    /// Fetches <c>/@latest</c> for <paramref name="modulePath"/>, walking up the path
    /// on 404 to handle <c>cmd/</c> subpackage paths that are not themselves module roots.
    /// Returns <see langword="null"/> when no ancestor path resolves to a module.
    /// </summary>
    private static async Task<GoModuleLatest?> FetchModuleLatestAsync(
        HttpClient client,
        string modulePath,
        CancellationToken cancellationToken)
    {
        var segments = modulePath.Split('/');

        // VCS-hosted modules need at least 3 segments (e.g. github.com/org/repo).
        for (var len = segments.Length; len >= Math.Min(3, segments.Length); len--)
        {
            var candidate = string.Join('/', segments, 0, len);
            var response = await client
                .GetAsync($"/{candidate}/@latest", cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content
                    .ReadFromJsonAsync(GoProxyJsonContext.Default.GoModuleLatest, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
            }
        }

        return null;
    }

    private static UpdateCheckResult Err(AppRecord app, string msg)
        => new(app.Name, UpdateMethod.PackageRegistry, false, app.InstalledVersion, null, msg);
}

internal sealed class GoModuleLatest
{
    [JsonPropertyName("Version")]
    public string? Version { get; init; }
}

[JsonSerializable(typeof(GoModuleLatest))]
internal sealed partial class GoProxyJsonContext : JsonSerializerContext;

