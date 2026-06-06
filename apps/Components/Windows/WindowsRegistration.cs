using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.Windows;

/// <summary>Registers Windows-native scanners (installed applications discovered from the registry and winget).</summary>
public static class WindowsRegistration
{
    /// <summary>Adds all Windows-native scanners.</summary>
    public static IServiceCollection AddWindows(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, WindowsApplicationsScanner>();
        return services;
    }
}

