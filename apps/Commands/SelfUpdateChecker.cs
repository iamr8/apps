using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

using apps.Infrastructure;

namespace apps.Commands;

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
    /// </summary>
    public static async Task CheckForUpdateAsync(IHttpClientFactory httpClientFactory, CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("github");
            var response = await client
                .GetAsync($"/repos/{RepoOwner}/{RepoName}/releases/latest", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var release = await response.Content
                .ReadFromJsonAsync(SelfUpdateJsonContext.Default.SelfUpdateRelease, cancellationToken)
                .ConfigureAwait(false);

            var latestTag = release?.TagName?.TrimStart('v');

            if (string.IsNullOrWhiteSpace(latestTag))
            {
                return;
            }

            if (VersionComparer.IsNewer(CurrentVersion, latestTag))
            {
                var url = $"{ReleasesUrl}v{latestTag}";
                Console.WriteLine();
                Console.WriteLine($"\e[33m⚡ A new version of apps is available: v{latestTag} (current: v{CurrentVersion})\e[0m");
                Console.WriteLine($"\e[33m   {url}\e[0m");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Self-update check is best-effort; swallow failures silently.
        }
    }
}

internal sealed record SelfUpdateRelease(
    [property: JsonPropertyName("tag_name")]
    string? TagName);

[JsonSerializable(typeof(SelfUpdateRelease))]
internal sealed partial class SelfUpdateJsonContext : JsonSerializerContext;