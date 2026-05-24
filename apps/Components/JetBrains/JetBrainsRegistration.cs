using apps.Checkers;
using apps.Infrastructure;
using apps.Scanners;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.JetBrains;

/// <summary>Registers JetBrains IDE plugins platform scanner and checker.</summary>
public static class JetBrainsRegistration
{
    /// <summary>Adds the JetBrains plugin scanner and plugin repository checker.</summary>
    public static IServiceCollection AddJetBrainsPlatform(this IServiceCollection services)
    {
        services.AddCheckerClient("jetbrains", "https://plugins.jetbrains.com", 4);

        services.AddSingleton<IScanner, JetBrainsPluginScanner>();
        services.AddSingleton<IUpdateChecker, JetBrainsPluginChecker>();
        return services;
    }
}
