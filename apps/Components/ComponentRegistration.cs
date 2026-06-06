using apps.Components.Chrome;
using apps.Components.Docker;
using apps.Components.Dotnet;
using apps.Components.Go;
using apps.Components.JetBrains;
using apps.Components.MacOs;
using apps.Components.Node;
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
        services.AddDocker();
        services.AddVsCode();
        services.AddJetBrains();
        services.AddMacOs();
        services.AddWindows();
        services.AddChrome();
        return services;
    }
}