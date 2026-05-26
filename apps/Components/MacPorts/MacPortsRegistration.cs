using apps.Checkers;
using apps.Scanners;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.MacPorts;

/// <summary>Registers MacPorts platform scanner and checker.</summary>
public static class MacPortsRegistration
{
    /// <summary>Adds the MacPorts scanner and outdated-check checker.</summary>
    public static IServiceCollection AddMacPorts(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, MacPortsScanner>();
        services.AddSingleton<IUpdateChecker, MacPortsChecker>();
        return services;
    }
}

