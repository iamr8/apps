using apps.Components.AppStore;
using apps.Components.Chocolatey;
using apps.Components.Docker;
using apps.Components.Dotnet;
using apps.Components.Electron;
using apps.Components.GitHub;
using apps.Components.Go;
using apps.Components.Homebrew;
using apps.Components.JetBrains;
using apps.Components.MacOs;
using apps.Components.MacPorts;
using apps.Components.Node;
using apps.Components.Sparkle;
using apps.Components.Swift;
using apps.Components.Vcpkg;
using apps.Components.VsCode;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components;

/// <summary>
/// Central entry point that registers all component slices into the DI container.
/// To add a new component, create its folder under <c>Components/</c>, implement
/// an <c>Add{Platform}</c> extension method, and chain it here.
/// </summary>
public static class ComponentRegistration
{
    /// <summary>Registers all component scanners, checkers, and HTTP clients.</summary>
    public static IServiceCollection AddAllComponents(this IServiceCollection services)
    {
        services.AddDotnetPlatform();
        services.AddNodePlatform();
        services.AddGoPlatform();
        services.AddHomebrewPlatform();
        services.AddAppStorePlatform();
        services.AddMacPortsPlatform();
        services.AddChocolateyPlatform();
        services.AddDockerPlatform();
        services.AddVsCodePlatform();
        services.AddJetBrainsPlatform();
        services.AddGitHubPlatform();
        services.AddSparklePlatform();
        services.AddElectronPlatform();
        services.AddMacOsPlatform();
        services.AddSwiftPlatform();
        services.AddVcpkgPlatform();
        return services;
    }
}

