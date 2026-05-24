using apps.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.Audit;

/// <summary>Registers the CVE audit checker and its HTTP client.</summary>
public static class AuditRegistration
{
    /// <summary>Adds the OSV audit checker and its HTTP client.</summary>
    public static IServiceCollection AddAuditComponent(this IServiceCollection services)
    {
        services.AddCheckerClient("osv", "https://api.osv.dev", 4, c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("apps/1.0");
        });

        services.AddSingleton<OsvAuditChecker>();
        return services;
    }
}

