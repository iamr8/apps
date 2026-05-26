using apps.Scanners;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.Swift;

/// <summary>Registers the Swift Package Manager project-level scanner.</summary>
public static class SwiftRegistration
{
    /// <summary>Adds the Swift Package.swift scanner.</summary>
    public static IServiceCollection AddSwift(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, SwiftPackageScanner>();
        return services;
    }
}

