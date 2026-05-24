using apps.Checkers;
using apps.Infrastructure;
using apps.Scanners;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.AppStore;

/// <summary>Registers App Store platform scanner and checker.</summary>
public static class AppStoreRegistration
{
    /// <summary>Adds the App Store scanner and iTunes lookup checker.</summary>
    public static IServiceCollection AddAppStorePlatform(this IServiceCollection services)
    {
        services.AddCheckerClient("itunes", "https://itunes.apple.com", 6);

        services.AddSingleton<IScanner, AppStoreScanner>();
        services.AddSingleton<IUpdateChecker, AppStoreChecker>();
        return services;
    }
}
