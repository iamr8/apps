using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

using apps.Infrastructure;
using apps.Checkers;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Node;

/// <summary>
/// Priority 8 checker — queries the npm registry for the latest published version
/// of each package.
///
/// Handles apps from the following scanners: <c>npm</c> (globally installed packages)
/// and <c>NpmProject</c> (project dependencies — opt-in).
///
/// Uses <c>GET https://registry.npmjs.org/{name}/latest</c> with the lightweight
/// install manifest Accept header. The endpoint is Cloudflare-fronted and effectively
/// unlimited for reads.
/// All checks fan out concurrently via the shared <c>"npm"</c> named client.
/// In-flight request coalescing ensures each package name is fetched at most once per run.
/// </summary>
public sealed class NpmRegistryChecker(IHttpClientFactory httpClientFactory, ILogger<NpmRegistryChecker> logger)
    : IUpdateChecker
{
    private static readonly HashSet<string> NpmScanners = new(StringComparer.OrdinalIgnoreCase)
    {
        "npm", "NpmProject"
    };

    private readonly ConcurrentDictionary<string, Task<string?>> _inflightLatest = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public UpdateMethod Method => UpdateMethod.PackageRegistry;

    /// <inheritdoc/>
    public string DisplayName => "npm";

    /// <inheritdoc/>
    public bool CanCheck(AppRecord app)
        => app.UpdateMethod == UpdateMethod.PackageRegistry
           && NpmScanners.Contains(app.Identifier.Name);

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
        var packageName = app.UpdateMethodDetail ?? app.Name;

        try
        {
            var latest = await _inflightLatest.GetOrAdd(packageName, name => FetchLatestVersionAsync(name, cancellationToken)).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(latest))
            {
                return Err(app, "npm registry returned no version");
            }

            var updateAvailable = VersionComparer.IsNewer(app.InstalledVersion, latest);
            return new UpdateCheckResult(app.Name, UpdateMethod.PackageRegistry, updateAvailable, app.InstalledVersion, latest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "npm registry check failed for {Name}",
                packageName);
            return Err(app, ex.Message);
        }
    }

    private async Task<string?> FetchLatestVersionAsync(string packageName, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("npm");
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildPath(packageName));
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/json");

        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var info = await response.Content
            .ReadFromJsonAsync(NpmJsonContext.Default.NpmPackageLatest, cancellationToken)
            .ConfigureAwait(false);

        return info?.Version;
    }

    /// <summary>
    /// Builds the registry path for a package name, URL-encoding the '/' in scoped
    /// packages so that <c>@scope/name</c> becomes <c>/@scope%2Fname/latest</c>.
    /// </summary>
    private static string BuildPath(string packageName)
    {
        if (!packageName.StartsWith("@", StringComparison.Ordinal))
        {
            return $"/{packageName}/latest";
        }

        var slashIdx = packageName.IndexOf('/', 1);

        if (slashIdx < 0)
        {
            return $"/{packageName}/latest";
        }

        var scope = packageName[..slashIdx];      // "@scope"
        var name = packageName[(slashIdx + 1)..]; // "name"
        return $"/{scope}%2F{name}/latest";
    }

    private static UpdateCheckResult Err(AppRecord app, string msg)
        => new(app.Name, UpdateMethod.PackageRegistry, false, app.InstalledVersion, null, msg);
}

internal sealed class NpmPackageLatest
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

[JsonSerializable(typeof(NpmPackageLatest))]
internal sealed partial class NpmJsonContext : JsonSerializerContext;

