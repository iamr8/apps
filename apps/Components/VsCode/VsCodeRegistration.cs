using apps.Checkers;
using apps.Infrastructure;
using apps.Scanners;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.VsCode;

/// <summary>Registers VS Code extensions platform scanner and checker.</summary>
public static class VsCodeRegistration
{
    /// <summary>Adds the VS Code extension scanner and marketplace checker.</summary>
    public static IServiceCollection AddVsCode(this IServiceCollection services)
    {
        services.AddCheckerClient("vscode", "https://marketplace.visualstudio.com", 2);

        services.AddSingleton<IScanner, VsCodeExtScanner>();
        services.AddSingleton<IUpdateChecker, VsCodeExtChecker>();
        return services;
    }
}
