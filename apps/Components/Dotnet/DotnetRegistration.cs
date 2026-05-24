using apps.Checkers;
using apps.Infrastructure;
using apps.Scanners;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.Dotnet;

/// <summary>Registers .NET platform scanners, checkers, and HTTP clients.</summary>
public static class DotnetRegistration
{
    /// <summary>Adds the .NET SDK scanner, runtime scanner, NuGet scanners, releases API checker, and NuGet registry checker.</summary>
    public static IServiceCollection AddDotnetPlatform(this IServiceCollection services)
    {
        services.AddCheckerClient("nuget", "https://api.nuget.org", 24);
        services.AddCheckerClient("dotnet-releases", "https://dotnetcli.blob.core.windows.net", 4);

        services.AddSingleton<IScanner, DotnetScanner>();
        services.AddSingleton<IScanner, DotnetRuntimeScanner>();
        services.AddSingleton<IScanner, NugetGlobalToolsScanner>();
        services.AddSingleton<IProjectLevelScanner, NugetProjectScanner>();
        services.AddSingleton<IProjectLevelScanner, NugetLocalToolsScanner>();
        services.AddSingleton<IUpdateChecker, DotnetReleasesChecker>();
        services.AddSingleton<IUpdateChecker, NugetRegistryChecker>();
        return services;
    }
}
