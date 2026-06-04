using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace apps;

/// <summary>
/// Checks whether a newer version of the apps CLI is available on GitHub Releases.
/// Compares the embedded assembly version against the latest release tag from
/// <c>https://github.com/iamr8/apps</c>.
/// </summary>
public static class SelfUpdateChecker
{
    private const string RepoOwner = "iamr8";
    private const string RepoName = "apps";

    /// <summary>Gets the current version of the running binary from the assembly informational version.</summary>
    public static string CurrentVersion
    {
        get
        {
            var attr = typeof(SelfUpdateChecker).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            var version = attr?.InformationalVersion ?? "0.0.0";
            var plusIdx = version.IndexOf('+');
            return plusIdx >= 0 ? version[..plusIdx] : version;
        }
    }

    /// <summary>
    /// Queries GitHub Releases for the latest version and prints a notice when a newer version is
    /// available, pointing the user at <c>apps --upgrade</c>. Used as a best-effort check at the end
    /// of a normal run; the actual upgrade is performed by <see cref="SelfUpdater"/>.
    /// </summary>
    public static async Task<SelfUpdateResult> CheckForUpdateAsync(IHttpClientFactory httpClientFactory, CancellationToken cancellationToken)
    {
        var info = await FetchLatestReleaseAsync(httpClientFactory, cancellationToken).ConfigureAwait(false);

        if (info.Result == SelfUpdateResult.UpdateAvailable)
        {
            Console.WriteLine();
            Console.WriteLine($"\e[33m⚡ A new version of apps is available: v{info.LatestVersion} (current: v{CurrentVersion})\e[0m");
            Console.WriteLine($"\e[33m   call apps (--upgrade|-u) to upgrade to v{info.LatestVersion}\e[0m");
        }

        return info.Result;
    }

    /// <summary>
    /// Queries GitHub Releases for the latest published release and compares it against the running
    /// binary, without printing anything. Returns the comparison outcome together with the version
    /// tag and downloadable assets so <c>--upgrade</c> can fetch the matching archive.
    /// </summary>
    internal static async Task<SelfUpdateInfo> FetchLatestReleaseAsync(IHttpClientFactory httpClientFactory, CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("github-api");
            var response = await client
                .GetAsync($"/repos/{RepoOwner}/{RepoName}/releases/latest", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new SelfUpdateInfo(SelfUpdateResult.CheckFailed, null, [], null);
            }

            var release = await response.Content
                .ReadFromJsonAsync(SelfUpdateJsonContext.Default.SelfUpdateRelease, cancellationToken)
                .ConfigureAwait(false);

            var latestTag = release?.TagName?.TrimStart('v');

            if (string.IsNullOrWhiteSpace(latestTag))
            {
                return new SelfUpdateInfo(SelfUpdateResult.CheckFailed, null, [], null);
            }

            IReadOnlyList<ReleaseAsset> assets = release!.Assets ?? [];
            var result = VersionComparer.IsNewer(CurrentVersion, latestTag)
                ? SelfUpdateResult.UpdateAvailable
                : SelfUpdateResult.UpToDate;

            return new SelfUpdateInfo(result, latestTag, assets, release.Body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Self-update check is best-effort; swallow failures silently.
            return new SelfUpdateInfo(SelfUpdateResult.CheckFailed, null, [], null);
        }
    }
}

/// <summary>Result of a latest-release lookup: the comparison outcome, version tag, assets, and changelog body.</summary>
internal readonly record struct SelfUpdateInfo(
    SelfUpdateResult Result,
    string? LatestVersion,
    IReadOnlyList<ReleaseAsset> Assets,
    string? Changelog);

/// <summary>Outcome of a self-update check against GitHub Releases.</summary>
public enum SelfUpdateResult
{
    /// <summary>The running binary is already at the latest published version.</summary>
    UpToDate,
    /// <summary>A newer version is available; the notice has already been printed.</summary>
    UpdateAvailable,
    /// <summary>The check could not complete (network error, rate limit, or unexpected response).</summary>
    CheckFailed
}

internal sealed record SelfUpdateRelease(
    [property: JsonPropertyName("tag_name")]
    string? TagName,
    [property: JsonPropertyName("body")]
    string? Body,
    [property: JsonPropertyName("assets")]
    ReleaseAsset[]? Assets);

/// <summary>A single downloadable asset attached to a GitHub release.</summary>
internal sealed record ReleaseAsset(
    [property: JsonPropertyName("name")]
    string? Name,
    [property: JsonPropertyName("browser_download_url")]
    string? DownloadUrl);

[JsonSerializable(typeof(SelfUpdateRelease))]
internal sealed partial class SelfUpdateJsonContext : JsonSerializerContext;