using apps.Components.AppStore;
using apps.Components.Chocolatey;
using apps.Components.Chrome;
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
using apps.Components.Windows;

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
        services.AddDotnet();
        services.AddNode();
        services.AddGo();
        services.AddHomebrew();
        services.AddAppStore();
        services.AddMacPorts();
        services.AddChocolatey();
        services.AddDocker();
        services.AddVsCode();
        services.AddJetBrains();
        services.AddGitHub();
        services.AddSparkle();
        services.AddElectron();
        services.AddMacOs();
        services.AddWindows();
        services.AddSwift();
        services.AddVcpkg();
        services.AddChrome();
        return services;
    }
}

