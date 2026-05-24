using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

using apps.Infrastructure;
using apps.Checkers;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Electron;

/// <summary>
/// Checks for updates for Electron apps discovered by <c>ElectronScanner</c>.
///
/// <para>Two providers are supported, identified by the prefix in <see cref="AppRecord.UpdateMethodDetail"/>:</para>
/// <list type="bullet">
///   <item>
///     <c>"github:{owner}/{repo}"</c> — queries
///     <c>GET https://api.github.com/repos/{owner}/{repo}/releases/latest</c>
///     and reads <c>tag_name</c>. Reuses the shared <c>"github"</c> named client so
///     it benefits from the same rate-limiting, token header, and concurrency cap.
///   </item>
///   <item>
///     <c>"generic:{url}"</c> — fetches <c>{url}/latest-mac.yml</c>
///     (falls back to <c>latest.yml</c> on 404) and parses the <c>version:</c> line.
///     Uses the <c>"electron-generic"</c> named client.
///   </item>
/// </list>
///
/// All checks fan out concurrently; results stream as each HTTP response arrives.
/// </summary>
public sealed class ElectronChecker(IHttpClientFactory httpClientFactory, ILogger<ElectronChecker> logger)
    : IUpdateChecker
{
    public UpdateMethod Method => UpdateMethod.Electron;

    /// <inheritdoc/>
    public string DisplayName => "Electron";

    /// <inheritdoc/>
    public bool CanCheck(AppRecord app)
        => app is { UpdateMethod: UpdateMethod.Electron, UpdateMethodDetail: not null };

    /// <inheritdoc/>
    public async Task<UpdateCheckResult> CheckAsync(AppRecord app, CancellationToken cancellationToken = default)
    {
        return await CheckOneAsync(app, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<UpdateCheckResult>> CheckBatchAsync(IReadOnlyList<AppRecord> apps, CancellationToken cancellationToken = default)
    {
        var tasks = apps.Select(a => CheckOneAsync(a, cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Uses <see cref="Task.WhenEach"/> to stream results as each HTTP response arrives
    /// rather than waiting for the full batch.
    /// </remarks>
    public async IAsyncEnumerable<UpdateCheckResult> CheckStreamAsync(IReadOnlyList<AppRecord> apps, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var task in Task.WhenEach(apps.Select(a => CheckOneAsync(a, cancellationToken))).ConfigureAwait(false))
        {
            yield return await task.ConfigureAwait(false);
        }
    }

    private async Task<UpdateCheckResult> CheckOneAsync(AppRecord app, CancellationToken cancellationToken)
    {
        var detail = app.UpdateMethodDetail!;

        try
        {
            string? latest;

            if (detail.StartsWith("github:", StringComparison.Ordinal))
            {
                latest = await CheckGitHubAsync(detail["github:".Length..], cancellationToken).ConfigureAwait(false);
            }
            else if (detail.StartsWith("generic:", StringComparison.Ordinal))
            {
                latest = await CheckGenericAsync(detail["generic:".Length..], cancellationToken).ConfigureAwait(false);

                if (latest is null)
                {
                    // Neither latest-mac.yml nor latest.yml were found; the app's generic
                    // feed is not published. Skip silently — not an error condition.
                    logger.LogDebug(
                        "Electron generic feed not found for {Name} at {Url}; skipping",
                        app.Name,
                        detail["generic:".Length..]);
                    return new UpdateCheckResult(app.Name, UpdateMethod.Electron, false, app.InstalledVersion, app.InstalledVersion);
                }
            }
            else
            {
                return Error(app, $"Unknown provider in detail: {detail}");
            }

            if (latest is null)
            {
                return Error(app, "Could not resolve latest version from feed");
            }

            var updateAvailable = VersionComparer.IsNewer(app.InstalledVersion, latest);
            return new UpdateCheckResult(app.Name, UpdateMethod.Electron, updateAvailable, app.InstalledVersion, latest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Electron check failed for {Name}",
                app.Name);
            return Error(app, ex.Message);
        }
    }

    private async Task<string?> CheckGitHubAsync(string ownerRepo, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("github");
        var response = await client
            .GetAsync($"/repos/{ownerRepo}/releases/latest", cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var release = await response.Content
            .ReadFromJsonAsync(ElectronJsonContext.Default.GitHubReleaseResponse, cancellationToken)
            .ConfigureAwait(false);

        return release?.TagName?.TrimStart('v');
    }

    private async Task<string?> CheckGenericAsync(string baseUrl, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("electron-generic");
        var trimmed = baseUrl.TrimEnd('/');

        // Try platform-specific file first, then the generic fallback.
        foreach (var filename in new[] { "latest-mac.yml", "latest.yml" })
        {
            try
            {
                var content = await client
                    .GetStringAsync($"{trimmed}/{filename}", cancellationToken)
                    .ConfigureAwait(false);
                return ParseVersionLine(content);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // This filename doesn't exist; try the next one.
            }
        }

        // Neither file exists — feed is not published at this URL.
        return null;
    }

    private static string? ParseVersionLine(string yml)
    {
        foreach (var raw in yml.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colonIdx = raw.IndexOf(':');
            if (colonIdx < 0)
            {
                continue;
            }

            var key = raw[..colonIdx].Trim();
            if (!key.Equals("version", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = raw[(colonIdx + 1)..].Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        return null;
    }

    private static UpdateCheckResult Error(AppRecord app, string message)
        => new(app.Name, UpdateMethod.Electron, false, app.InstalledVersion, null, message);
}

/// <summary>Minimal projection of the GitHub Releases API <c>/releases/latest</c> response.</summary>
internal sealed record GitHubReleaseResponse(
    [property: JsonPropertyName("tag_name")]
    string? TagName);

/// <summary>Source-generated JSON serializer context for <see cref="ElectronChecker"/> (AOT-safe).</summary>
[JsonSerializable(typeof(GitHubReleaseResponse))]
internal sealed partial class ElectronJsonContext : JsonSerializerContext;
