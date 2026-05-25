using apps.Checkers;
using apps.Infrastructure;
using apps.Scanners;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.Homebrew;

/// <summary>Registers Homebrew platform scanner and checkers (Cask + Formula).</summary>
public static class HomebrewRegistration
{
    /// <summary>Adds the Homebrew scanner and both cask/formula checkers.</summary>
    public static IServiceCollection AddHomebrewPlatform(this IServiceCollection services)
    {
        services.AddCheckerClient("homebrew-api", "https://formulae.brew.sh", 4);

        services.AddSingleton<IScanner, HomebrewScanner>();
        services.AddSingleton<IUpdateChecker, HomebrewCaskChecker>();
        services.AddSingleton<IUpdateChecker, HomebrewFormulaChecker>();
        return services;
    }
}

