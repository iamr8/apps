using apps.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.Audit;

/// <summary>Registers the CVE audit checker and its HTTP client.</summary>
public static class AuditRegistration
{
    /// <summary>Adds the OSV audit checker, GitHub Advisory enricher, and their HTTP clients.</summary>
    public static IServiceCollection AddAuditComponent(this IServiceCollection services)
    {
        services.AddCheckerClient("osv", "https://api.osv.dev", 4, c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("apps/1.0");
        });

        services.AddCheckerClient("github-advisory", "https://api.github.com", 2, c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("apps/1.0");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        });

        services.AddSingleton<OsvAuditChecker>();
        services.AddSingleton<GitHubAdvisoryEnricher>();
        return services;
    }
}

