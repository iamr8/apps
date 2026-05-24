using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

using apps.Infrastructure;
using apps.Checkers;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.JetBrains;

/// <summary>
/// Specialised checker — queries <c>GET https://plugins.jetbrains.com/api/plugins/{id}/updates</c>
/// for the latest published version of each JetBrains IDE plugin.
///
/// Handles apps from the <c>JetBrains</c> scanner. <see cref="AppRecord.UpdateMethodDetail"/>
/// holds the plugin XML ID (either a numeric ID or a string like
/// <c>"org.jetbrains.plugins.go"</c>). The updates endpoint only accepts numeric IDs,
/// so string XML IDs are resolved to a numeric ID first via the search API.
///
/// The <see cref="RateLimitedHttpHandler"/> enforces a 4 req/s token-bucket limit for this
/// host to stay within JetBrains' safe rate. All checks fan out concurrently within
/// that budget.
/// </summary>
public sealed class JetBrainsPluginChecker(IHttpClientFactory httpClientFactory, ILogger<JetBrainsPluginChecker> logger)
    : IUpdateChecker
{
    /// <inheritdoc/>
    public UpdateMethod Method => UpdateMethod.Specialised;

    /// <inheritdoc/>
    public string DisplayName => "JetBrains Plugins";

    /// <inheritdoc/>
    public bool CanCheck(AppRecord app)
        => app.UpdateMethod == UpdateMethod.Specialised
           && string.Equals(app.Scanner, "JetBrains", StringComparison.Ordinal)
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
        var xmlId = app.UpdateMethodDetail!;

        try
        {
            using var client = httpClientFactory.CreateClient("jetbrains");

            // Resolve to numeric plugin ID when the stored ID is a string XML ID.
            // The /updates endpoint only accepts numeric IDs; string IDs return 400.
            string numericId;

            if (IsNumeric(xmlId))
            {
                numericId = xmlId;
            }
            else
            {
                var resolved = await ResolveNumericIdAsync(client, xmlId, cancellationToken).ConfigureAwait(false);

                if (resolved is null)
                {
                    // Plugin is not in the public marketplace (bundled, internal, or private).
                    // Return up-to-date silently — no actionable update is available.
                    return new UpdateCheckResult(app.Name, UpdateMethod.Specialised, false, app.InstalledVersion, app.InstalledVersion);
                }

                numericId = resolved;
            }

            var updates = await client
                .GetFromJsonAsync(
                    $"/api/plugins/{numericId}/updates?channel=&size=1",
                    JetBrainsJsonContext.Default.JetBrainsPluginUpdateArray,
                    cancellationToken)
                .ConfigureAwait(false);

            var latest = updates?.FirstOrDefault()?.Version;

            if (string.IsNullOrWhiteSpace(latest))
            {
                return Err(app, "JetBrains plugin repository returned no version");
            }

            var updateAvailable = VersionComparer.IsNewer(app.InstalledVersion, latest);
            return new UpdateCheckResult(app.Name, UpdateMethod.Specialised, updateAvailable, app.InstalledVersion, latest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "JetBrains plugin check failed for {Name} (id={Id})",
                app.Name,
                xmlId);
            return Err(app, ex.Message);
        }
    }

    /// <summary>
    /// Searches the JetBrains plugin repository by XML ID and returns the numeric plugin ID,
    /// or <see langword="null"/> when the plugin is not publicly listed or the ID is not recognised.
    /// </summary>
    private async Task<string?> ResolveNumericIdAsync(HttpClient client, string xmlId, CancellationToken cancellationToken)
    {
        var response = await client
            .GetAsync($"/api/plugins?xmlId={Uri.EscapeDataString(xmlId)}&size=1", cancellationToken)
            .ConfigureAwait(false);

        // 400 or 404 means the marketplace does not recognise this XML ID
        // (bundled, internal, or private plugin).
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var searchResult = await response.Content
            .ReadFromJsonAsync(JetBrainsJsonContext.Default.JetBrainsPluginInfoArray, cancellationToken)
            .ConfigureAwait(false);

        var id = searchResult?.FirstOrDefault()?.Id;
        return id.HasValue ? id.Value.ToString() : null;
    }

    private static bool IsNumeric(string value)
    {
        foreach (var ch in value)
        {
            if (!char.IsAsciiDigit(ch))
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    private static UpdateCheckResult Err(AppRecord app, string msg)
        => new(app.Name, UpdateMethod.Specialised, false, app.InstalledVersion, null, msg);
}

internal sealed class JetBrainsPluginUpdate
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

/// <summary>Minimal projection of the JetBrains marketplace plugin search response.</summary>
internal sealed class JetBrainsPluginInfo
{
    [JsonPropertyName("id")]
    public int? Id { get; init; }
}

[JsonSerializable(typeof(JetBrainsPluginUpdate[]))]
[JsonSerializable(typeof(JetBrainsPluginInfo[]))]
internal sealed partial class JetBrainsJsonContext : JsonSerializerContext;

