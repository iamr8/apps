using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using apps.Infrastructure;
using apps.Checkers;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.GitHub;

/// <summary>
/// Priority 6 checker — uses the GitHub GraphQL API to batch-fetch the latest
/// release tag for up to 100 repositories per request, dramatically reducing
/// round-trips compared to individual REST calls.
///
/// Falls back to the REST <c>/repos/{owner}/{repo}/releases/latest</c> endpoint
/// when GraphQL fails or no <c>GITHUB_TOKEN</c> is available (GraphQL requires auth).
///
/// Uses the shared <c>"github"</c> named HttpClient, which includes:
/// <list type="bullet">
///   <item><c>GITHUB_TOKEN</c> authorization header when the env var is set (5 000 req/hr).</item>
///   <item><see cref="RateLimitedHttpHandler"/> concurrency cap (10 concurrent requests).</item>
/// </list>
/// </summary>
public sealed class GitHubReleasesChecker(IHttpClientFactory httpClientFactory, ILogger<GitHubReleasesChecker> logger)
    : IUpdateChecker
{
    private const int GraphQlBatchSize = 100;
    private static readonly bool HasToken = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_TOKEN"));

    /// <inheritdoc/>
    public UpdateMethod Method => UpdateMethod.GitHub;

    /// <inheritdoc/>
    public string DisplayName => "GitHub Releases";

    /// <inheritdoc/>
    public (string Label, string? Qualifier)? SourceOverride => ("GitHub", null);

    /// <inheritdoc/>
    public bool CanCheck(AppRecord app)
        => app is { UpdateMethod: UpdateMethod.GitHub, UpdateMethodDetail: not null };

    /// <inheritdoc/>
    public async Task<UpdateCheckResult> CheckAsync(AppRecord app, CancellationToken cancellationToken = default)
        => await CheckOneRestAsync(app, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<UpdateCheckResult>> CheckBatchAsync(
        IReadOnlyList<AppRecord> apps,
        CancellationToken cancellationToken = default)
    {
        if (HasToken && apps.Count > 1)
        {
            return await CheckBatchGraphQlAsync(apps, cancellationToken).ConfigureAwait(false);
        }

        var tasks = apps.Select(a => CheckOneRestAsync(a, cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<UpdateCheckResult> CheckStreamAsync(
        IReadOnlyList<AppRecord> apps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (HasToken && apps.Count > 1)
        {
            foreach (var result in await CheckBatchGraphQlAsync(apps, cancellationToken).ConfigureAwait(false))
            {
                yield return result;
            }

            yield break;
        }

        await foreach (var task in Task.WhenEach(apps.Select(a => CheckOneRestAsync(a, cancellationToken))).ConfigureAwait(false))
        {
            yield return await task.ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<UpdateCheckResult>> CheckBatchGraphQlAsync(
        IReadOnlyList<AppRecord> apps,
        CancellationToken cancellationToken)
    {
        var resultMap = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var chunks = apps
            .Select(a => a.UpdateMethodDetail!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Chunk(GraphQlBatchSize)
            .Select(chunk => FetchGraphQlBatchAsync(chunk, resultMap, cancellationToken))
            .ToList();

        await Task.WhenAll(chunks).ConfigureAwait(false);

        var results = new UpdateCheckResult[apps.Count];

        for (var i = 0; i < apps.Count; i++)
        {
            var app = apps[i];
            var ownerRepo = app.UpdateMethodDetail!;

            if (resultMap.TryGetValue(ownerRepo, out var tagName) && tagName is not null)
            {
                var latest = tagName.TrimStart('v');
                var updateAvailable = VersionComparer.IsNewer(app.InstalledVersion, latest);
                results[i] = new UpdateCheckResult(app.Name, UpdateMethod.GitHub, updateAvailable, app.InstalledVersion, latest);
            }
            else
            {
                results[i] = await CheckOneRestAsync(app, cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

    private async Task FetchGraphQlBatchAsync(
        string[] ownerRepos,
        ConcurrentDictionary<string, string?> resultMap,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = BuildGraphQlQuery(ownerRepos);
            using var client = httpClientFactory.CreateClient("github");
            var payload = JsonSerializer.Serialize(
                new GhGraphQlRequest { Query = query },
                GhReleasesJsonContext.Default.GhGraphQlRequest);

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/graphql", content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("GitHub GraphQL returned {Status}, falling back to REST", response.StatusCode);
                return;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                return;
            }

            for (var i = 0; i < ownerRepos.Length; i++)
            {
                var alias = $"r{i}";

                if (!data.TryGetProperty(alias, out var repoEl))
                {
                    continue;
                }

                if (repoEl.ValueKind == JsonValueKind.Null)
                {
                    resultMap[ownerRepos[i]] = null;
                    continue;
                }

                if (repoEl.TryGetProperty("latestRelease", out var release) && release.ValueKind != JsonValueKind.Null)
                {
                    if (release.TryGetProperty("tagName", out var tag))
                    {
                        resultMap[ownerRepos[i]] = tag.GetString();
                        continue;
                    }
                }

                resultMap[ownerRepos[i]] = null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "GitHub GraphQL batch fetch failed, individual REST fallback will be used");
        }
    }

    private async Task<UpdateCheckResult> CheckOneRestAsync(AppRecord app, CancellationToken cancellationToken)
    {
        var ownerRepo = app.UpdateMethodDetail!;

        try
        {
            using var client = httpClientFactory.CreateClient("github");
            var response = await client
                .GetAsync($"/repos/{ownerRepo}/releases/latest", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "GitHub API {Status} for {Repo}",
                    response.StatusCode,
                    ownerRepo);
                return Err(app, $"GitHub API returned {(int)response.StatusCode}");
            }

            var release = await response.Content
                .ReadFromJsonAsync(GhReleasesJsonContext.Default.GhLatestRelease, cancellationToken)
                .ConfigureAwait(false);

            var latest = release?.TagName?.TrimStart('v');

            if (string.IsNullOrWhiteSpace(latest))
            {
                return Err(app, "GitHub release has no tag_name");
            }

            var updateAvailable = VersionComparer.IsNewer(app.InstalledVersion, latest);
            return new UpdateCheckResult(app.Name, UpdateMethod.GitHub, updateAvailable, app.InstalledVersion, latest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "GitHub release check failed for {Name} ({Repo})",
                app.Name,
                ownerRepo);
            return Err(app, ex.Message);
        }
    }

    private static string BuildGraphQlQuery(string[] ownerRepos)
    {
        var sb = new StringBuilder("query{", ownerRepos.Length * 80);

        for (var i = 0; i < ownerRepos.Length; i++)
        {
            var parts = ownerRepos[i].Split('/');
            if (parts.Length != 2)
            {
                continue;
            }

            sb.Append($"r{i}:repository(owner:\"{parts[0]}\",name:\"{parts[1]}\"){{latestRelease{{tagName}}}}");
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static UpdateCheckResult Err(AppRecord app, string msg)
        => new(app.Name, UpdateMethod.GitHub, false, app.InstalledVersion, null, msg);
}

internal sealed record GhLatestRelease(
    [property: JsonPropertyName("tag_name")] string? TagName,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("draft")] bool Draft);

internal sealed class GhGraphQlRequest
{
    [JsonPropertyName("query")]
    public string Query { get; init; } = "";
}

[JsonSerializable(typeof(GhLatestRelease))]
[JsonSerializable(typeof(GhGraphQlRequest))]
internal sealed partial class GhReleasesJsonContext : JsonSerializerContext;

