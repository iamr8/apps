using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

using apps.Infrastructure;
using apps.Checkers;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Dotnet;

/// <summary>
/// Platform-agnostic .NET SDK/Runtime update checker that queries the official
/// .NET release metadata API at <c>dotnetcli.blob.core.windows.net</c>.
///
/// For each installed SDK or runtime, determines the channel (e.g. <c>8.0</c>)
/// and fetches the channel's <c>releases.json</c> to find the latest available version.
/// This replaces the CLI-dependent <c>dotnet sdk check</c> approach with a pure HTTP solution.
/// </summary>
public sealed class DotnetReleasesChecker(IHttpClientFactory httpClientFactory, ILogger<DotnetReleasesChecker> logger)
    : IUpdateChecker
{
    private const string ReleasesIndexUrl = "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json";

    /// <inheritdoc/>
    public UpdateMethod Method => UpdateMethod.Sdk;

    /// <inheritdoc/>
    public string DisplayName => ".NET Releases";

    /// <inheritdoc/>
    public bool CanCheck(AppRecord app)
        => app.UpdateMethod == UpdateMethod.Sdk
           && (string.Equals(app.Scanner, "Dotnet", StringComparison.Ordinal)
               || string.Equals(app.Scanner, "DotnetRuntime", StringComparison.Ordinal));

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
        var channelLookup = await FetchChannelLookupAsync(apps, cancellationToken).ConfigureAwait(false);
        return apps.Select(app => BuildResult(app, channelLookup)).ToArray();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<UpdateCheckResult> CheckStreamAsync(
        IReadOnlyList<AppRecord> apps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channelLookup = await FetchChannelLookupAsync(apps, cancellationToken).ConfigureAwait(false);

        foreach (var app in apps)
        {
            yield return BuildResult(app, channelLookup);
        }
    }

    /// <summary>
    /// Fetches the releases index, determines which channels are needed for the given apps,
    /// and retrieves each needed channel's latest SDK and runtime versions.
    /// Returns a lookup of channel → (latestSdk, latestRuntime).
    /// </summary>
    private async Task<Dictionary<string, ChannelLatest>> FetchChannelLookupAsync(
        IReadOnlyList<AppRecord> apps,
        CancellationToken cancellationToken)
    {
        var lookup = new Dictionary<string, ChannelLatest>(StringComparer.OrdinalIgnoreCase);

        DotnetReleasesIndex? index;
        try
        {
            using var client = httpClientFactory.CreateClient("dotnet-releases");
            index = await client
                .GetFromJsonAsync(ReleasesIndexUrl, DotnetJsonContext.Default.DotnetReleasesIndex, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch .NET releases index");
            return lookup;
        }

        if (index?.ReleasesIndex is null)
        {
            return lookup;
        }

        var neededChannels = apps
            .Select(a => ExtractChannel(a.InstalledVersion))
            .Where(c => c is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in index.ReleasesIndex)
        {
            if (channel.ChannelVersion is null || !neededChannels.Contains(channel.ChannelVersion))
            {
                continue;
            }

            lookup[channel.ChannelVersion] = new ChannelLatest(channel.LatestSdk, channel.LatestRuntime);
        }

        return lookup;
    }

    private static UpdateCheckResult BuildResult(AppRecord app, Dictionary<string, ChannelLatest> channelLookup)
    {
        if (app.InstalledVersion is null)
        {
            return Err(app, "No installed version");
        }

        var channel = ExtractChannel(app.InstalledVersion);
        if (channel is null || !channelLookup.TryGetValue(channel, out var latest))
        {
            return Err(app, $"Channel '{channel}' not found in .NET release metadata");
        }

        var isSdk = app.Name.Contains("SDK", StringComparison.OrdinalIgnoreCase);
        var latestVersion = isSdk ? latest.Sdk : latest.Runtime;

        if (string.IsNullOrWhiteSpace(latestVersion))
        {
            return new UpdateCheckResult(app.Name, UpdateMethod.Sdk, false, app.InstalledVersion, app.InstalledVersion);
        }

        var updateAvailable = VersionComparer.IsNewer(app.InstalledVersion, latestVersion);
        return new UpdateCheckResult(app.Name, UpdateMethod.Sdk, updateAvailable, app.InstalledVersion, latestVersion);
    }

    /// <summary>
    /// Extracts the major.minor channel from a version string (e.g. <c>"8.0.300"</c> → <c>"8.0"</c>).
    /// </summary>
    private static string? ExtractChannel(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var firstDot = version.IndexOf('.');
        if (firstDot < 0)
        {
            return null;
        }

        var secondDot = version.IndexOf('.', firstDot + 1);
        return secondDot > 0 ? version[..secondDot] : version;
    }

    private static UpdateCheckResult Err(AppRecord app, string msg)
        => new(app.Name, UpdateMethod.Sdk, false, app.InstalledVersion, null, msg);

    private sealed record ChannelLatest(string? Sdk, string? Runtime);
}


