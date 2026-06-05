using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;

namespace apps.Components.Windows;

/// <summary>Registers Windows-native scanners.</summary>
public static class WindowsRegistration
{
    /// <summary>Adds all Windows-native scanners.</summary>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddWindows(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, WindowsApplicationsScanner>();
        return services;
    }
}

