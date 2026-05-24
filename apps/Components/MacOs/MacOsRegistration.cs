using apps.Checkers;
using apps.Scanners;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.MacOs;

/// <summary>Registers macOS-native scanners (Applications, Software Update, Safari, Chrome, Xcode) and the macOS update checker.</summary>
public static class MacOsRegistration
{
    /// <summary>Adds all macOS-native scanners and the Software Update checker.</summary>
    public static IServiceCollection AddMacOsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, ApplicationsScanner>();
        services.AddSingleton<IScanner, MacOsUpdateScanner>();
        services.AddSingleton<IScanner, SafariExtScanner>();
        services.AddSingleton<IScanner, ChromeExtScanner>();
        services.AddSingleton<IScanner, XcodeScanner>();
        services.AddSingleton<IUpdateChecker, MacOsUpdateChecker>();
        return services;
    }
}

