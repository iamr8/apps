using apps.Checkers;
using apps.Infrastructure;
using apps.Scanners;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.Electron;

/// <summary>Registers Electron app scanner and auto-updater checker.</summary>
public static class ElectronRegistration
{
    /// <summary>Adds the Electron app-update.yml scanner, update checker, and generic feed HTTP client.</summary>
    public static IServiceCollection AddElectronPlatform(this IServiceCollection services)
    {
        services.AddCheckerClient("electron-generic", "https://placeholder.invalid", 4);

        services.AddSingleton<IScanner, ElectronScanner>();
        services.AddSingleton<IUpdateChecker, ElectronChecker>();
        return services;
    }
}
