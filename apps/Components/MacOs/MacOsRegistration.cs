using apps.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.MacOs;

/// <summary>Registers macOS-native scanners (Applications, Homebrew, Software Update) and update checkers.</summary>
public static class MacOsRegistration
{
    /// <summary>Adds the macOS Applications scanner with integrated update checks.</summary>
    public static IServiceCollection AddMacOs(this IServiceCollection services)
    {
        services.AddCheckerClient("homebrew-api", "https://formulae.brew.sh", 4);
        services.AddCheckerClient("generic", "https://example.com", 4);
        services.AddCheckerClient("github", "https://www.github.com", 10);
        services.AddCheckerClient("itunes", "https://itunes.apple.com", 6);
        services.AddCheckerClient("sparkle", "https://example.com", 4);

        services.AddSingleton<IScanner, MacApplicationsScanner>();
        return services;
    }
}