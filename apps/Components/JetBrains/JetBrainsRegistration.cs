using apps.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.JetBrains;

/// <summary>Registers JetBrains IDE plugins platform scanner and checker.</summary>
public static class JetBrainsRegistration
{
    /// <summary>Adds the JetBrains plugin scanner with integrated marketplace checker.</summary>
    public static IServiceCollection AddJetBrains(this IServiceCollection services)
    {
        services.AddCheckerClient("jetbrains", "https://plugins.jetbrains.com", 4);

        services.AddSingleton<IScanner, JetBrainsPluginScanner>();
        return services;
    }
}
