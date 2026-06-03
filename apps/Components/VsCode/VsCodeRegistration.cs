using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.VsCode;

/// <summary>Registers VS Code extensions platform scanner and checker.</summary>
public static class VsCodeRegistration
{
    /// <summary>Adds the VS Code extension scanner and marketplace checker.</summary>
    public static IServiceCollection AddVsCode(this IServiceCollection services)
    {
        services.AddCheckerClient("vscode", "https://marketplace.visualstudio.com", 2, c =>
        {
            c.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        });

        services.AddSingleton<IScanner, VsCodeExtScanner>();
        return services;
    }
}
