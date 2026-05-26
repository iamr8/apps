using apps.Scanners;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.Chrome;

/// <summary>Registers macOS-native scanners (Applications, Software Update, Safari, Chrome, Xcode) and the macOS update checker.</summary>
public static class ChromeRegistration
{
    /// <summary>Adds all macOS-native scanners and the Software Update checker.</summary>
    public static IServiceCollection AddChrome(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, ChromeExtScanner>();
        return services;
    }
}

