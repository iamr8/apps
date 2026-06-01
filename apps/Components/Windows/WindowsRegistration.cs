using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.Windows;

/// <summary>Registers macOS-native scanners (Applications, Software Update, Safari, Chrome, Xcode) and the macOS update checker.</summary>
public static class WindowsRegistration
{
    /// <summary>Adds all macOS-native scanners and the Software Update checker.</summary>
    public static IServiceCollection AddWindows(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, WindowsApplicationsScanner>();
        return services;
    }
}

