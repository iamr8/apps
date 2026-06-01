using apps.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.Chrome;

/// <summary>Registers Chrome extension scanner and update checker.</summary>
public static class ChromeRegistration
{
    /// <summary>Adds Chrome extension scanner and the CRX update checker client.</summary>
    public static IServiceCollection AddChrome(this IServiceCollection services)
    {
        services.AddCheckerClient("chrome-update", "https://clients2.google.com", 6);

        services.AddSingleton<IScanner, ChromeExtScanner>();
        return services;
    }
}

