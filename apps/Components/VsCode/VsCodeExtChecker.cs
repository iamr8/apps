using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

using apps.Infrastructure;
using apps.Checkers;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.VsCode;

/// <summary>
/// Specialised checker — queries the VS Code Marketplace extension query API to find
/// the latest published version of each installed extension.
///
/// Handles apps from the <c>VSCode</c> scanner. <see cref="AppRecord.UpdateMethodDetail"/>
/// holds the extension ID in <c>"publisher.extensionName"</c> format.
///
/// Batches up to 100 extension IDs per HTTP POST to the gallery API, dramatically
/// reducing the number of round-trips. Results are correlated back to app records
/// by reconstructing the composite ID from the response.
/// </summary>
public sealed class VsCodeExtChecker(IHttpClientFactory httpClientFactory, ILogger<VsCodeExtChecker> logger)
    : IUpdateChecker
{
    private const string GalleryApiPath = "/_apis/public/gallery/extensionquery?api-version=7.2-preview.1";
    private const int BatchSize = 100;
    // FilterType 7 = ExtensionName; Flag 512 = IncludeLatestVersionOnly
    private const int FilterTypeExtensionName = 7;
    private const int FlagIncludeLatestVersionOnly = 512;

    /// <inheritdoc/>
    public UpdateMethod Method => UpdateMethod.Specialised;

    /// <inheritdoc/>
    public string DisplayName => "VS Code Marketplace";

    /// <inheritdoc/>
    public bool CanCheck(AppRecord app)
        => app.UpdateMethod == UpdateMethod.Specialised
           && string.Equals(app.Scanner, "VSCode", StringComparison.Ordinal);

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
        var resultMap = await FetchLatestVersionsAsync(apps, cancellationToken).ConfigureAwait(false);
        return apps.Select(app => BuildResult(app, resultMap)).ToArray();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<UpdateCheckResult> CheckStreamAsync(
        IReadOnlyList<AppRecord> apps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var resultMap = await FetchLatestVersionsAsync(apps, cancellationToken).ConfigureAwait(false);

        foreach (var app in apps)
        {
            yield return BuildResult(app, resultMap);
        }
    }

    private async Task<Dictionary<string, string>> FetchLatestVersionsAsync(
        IReadOnlyList<AppRecord> apps,
        CancellationToken cancellationToken)
    {
        var latestByExtId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var client = httpClientFactory.CreateClient("vscode");

        // Fan out batches concurrently — each batch is one HTTP POST
        var batches = apps
            .Select(a => a.UpdateMethodDetail ?? a.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Chunk(BatchSize)
            .Select(ids => FetchBatchAsync(client, ids, latestByExtId, cancellationToken))
            .ToList();

        await Task.WhenAll(batches).ConfigureAwait(false);
        return latestByExtId;
    }

    private async Task FetchBatchAsync(
        HttpClient client,
        string[] extensionIds,
        Dictionary<string, string> results,
        CancellationToken cancellationToken)
    {
        var criteria = extensionIds
            .Select(id => new VsCodeCriterion { FilterType = FilterTypeExtensionName, Value = id })
            .ToArray();

        var body = new VsCodeQueryRequest
        {
            Filters =
            [
                new VsCodeFilter
                {
                    Criteria = criteria,
                    PageSize = BatchSize,
                    PageNumber = 1
                }
            ],
            Flags = FlagIncludeLatestVersionOnly
        };

        try
        {
            var response = await client
                .PostAsJsonAsync(GalleryApiPath, body, VsCodeJsonContext.Default.VsCodeQueryRequest, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "VS Code Marketplace API returned {Status} for batch of {Count} extensions",
                    response.StatusCode,
                    extensionIds.Length);
                return;
            }

            var queryResult = await response.Content
                .ReadFromJsonAsync(VsCodeJsonContext.Default.VsCodeQueryResponse, cancellationToken)
                .ConfigureAwait(false);

            var extensions = queryResult?.Results?.FirstOrDefault()?.Extensions;

            if (extensions is null)
            {
                return;
            }

            lock (results)
            {
                foreach (var ext in extensions)
                {
                    var publisherName = ext.Publisher?.PublisherName;
                    var extensionName = ext.ExtensionName;
                    var version = ext.Versions?.FirstOrDefault()?.Version;

                    if (string.IsNullOrWhiteSpace(publisherName)
                        || string.IsNullOrWhiteSpace(extensionName)
                        || string.IsNullOrWhiteSpace(version))
                    {
                        continue;
                    }

                    var compositeId = $"{publisherName}.{extensionName}";
                    results[compositeId] = version;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "VS Code Marketplace batch fetch failed for {Count} extensions",
                extensionIds.Length);
        }
    }

    private static UpdateCheckResult BuildResult(AppRecord app, Dictionary<string, string> latestByExtId)
    {
        var extId = app.UpdateMethodDetail ?? app.Name;

        if (!latestByExtId.TryGetValue(extId, out var latest))
        {
            // Extension not found in marketplace response — treat as up to date
            return new UpdateCheckResult(app.Name, UpdateMethod.Specialised, false, app.InstalledVersion, app.InstalledVersion);
        }

        var updateAvailable = VersionComparer.IsNewer(app.InstalledVersion, latest);
        return new UpdateCheckResult(app.Name, UpdateMethod.Specialised, updateAvailable, app.InstalledVersion, latest);
    }
}



