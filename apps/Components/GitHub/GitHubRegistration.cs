using apps.Checkers;
using apps.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.GitHub;

/// <summary>Registers GitHub Releases checker (no dedicated scanner — used as a fallback update method).</summary>
public static class GitHubRegistration
{
    /// <summary>Adds the GitHub Releases checker and its authenticated HTTP client.</summary>
    public static IServiceCollection AddGitHub(this IServiceCollection services)
    {
        services.AddCheckerClient("github", "https://api.github.com", 10, c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("apps/1.0");
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
            {
                c.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }
        });

        services.AddSingleton<IUpdateChecker, GitHubReleasesChecker>();
        return services;
    }
}
