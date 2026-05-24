using apps.Checkers;
using apps.Scanners;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.Chocolatey;

/// <summary>Registers Chocolatey platform scanner and checker.</summary>
public static class ChocolateyRegistration
{
    /// <summary>Adds the Chocolatey scanner and outdated-check checker.</summary>
    public static IServiceCollection AddChocolateyPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, ChocoScanner>();
        services.AddSingleton<IUpdateChecker, ChocoChecker>();
        return services;
    }
}

