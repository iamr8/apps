using apps.Scanners;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.Vcpkg;

/// <summary>Registers the vcpkg project-level scanner.</summary>
public static class VcpkgRegistration
{
    /// <summary>Adds the vcpkg.json scanner.</summary>
    public static IServiceCollection AddVcpkg(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, VcpkgScanner>();
        return services;
    }
}

