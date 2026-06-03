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
    private const string ReleasesUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/tag/";

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
    /// Queries GitHub Releases for the latest version and prints a message if a newer version is available.
    /// Returns the outcome so callers (e.g. <c>--upgrade</c>) can decide whether to announce an
    /// up-to-date or failed check; the message for an available update is always printed here.
    /// </summary>
    public static async Task<SelfUpdateResult> CheckForUpdateAsync(IHttpClientFactory httpClientFactory, CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("github-api");
            var response = await client
                .GetAsync($"/repos/{RepoOwner}/{RepoName}/releases/latest", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return SelfUpdateResult.CheckFailed;
            }

            var release = await response.Content
                .ReadFromJsonAsync(SelfUpdateJsonContext.Default.SelfUpdateRelease, cancellationToken)
                .ConfigureAwait(false);

            var latestTag = release?.TagName?.TrimStart('v');

            if (string.IsNullOrWhiteSpace(latestTag))
            {
                return SelfUpdateResult.CheckFailed;
            }

            if (VersionComparer.IsNewer(CurrentVersion, latestTag))
            {
                var url = $"{ReleasesUrl}v{latestTag}";
                Console.WriteLine();
                Console.WriteLine($"\e[33m⚡ A new version of apps is available: v{latestTag} (current: v{CurrentVersion})\e[0m");
                Console.WriteLine($"\e[33m   {url}\e[0m");
                return SelfUpdateResult.UpdateAvailable;
            }

            return SelfUpdateResult.UpToDate;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Self-update check is best-effort; swallow failures silently.
            return SelfUpdateResult.CheckFailed;
        }
    }
}

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
    string? TagName);

[JsonSerializable(typeof(SelfUpdateRelease))]
internal sealed partial class SelfUpdateJsonContext : JsonSerializerContext;