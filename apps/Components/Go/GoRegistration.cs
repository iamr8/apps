using apps.Checkers;
using apps.Infrastructure;
using apps.Scanners;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.Go;

/// <summary>Registers Go platform scanners and checkers.</summary>
public static class GoRegistration
{
    /// <summary>Adds Go runtime scanner, Go tools scanner, Go module scanner, and Go proxy checker.</summary>
    public static IServiceCollection AddGo(this IServiceCollection services)
    {
        services.AddCheckerClient("goproxy", "https://proxy.golang.org", 24);

        services.AddSingleton<IScanner, GoScanner>();
        services.AddSingleton<IScanner, GoToolsScanner>();
        services.AddSingleton<IProjectLevelScanner, GoModScanner>();
        services.AddSingleton<IUpdateChecker, GoModProxyChecker>();
        return services;
    }
}
