using apps.Checkers;
using apps.Infrastructure;
using apps.Scanners;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.Node;

/// <summary>Registers Node.js/npm platform scanners and checkers.</summary>
public static class NodeRegistration
{
    /// <summary>Adds the Node scanner, npm global/project scanners, and npm registry checker.</summary>
    public static IServiceCollection AddNode(this IServiceCollection services)
    {
        services.AddCheckerClient("npm", "https://registry.npmjs.org", 32, c =>
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.npm.install-v1+json"));

        services.AddSingleton<IScanner, NodeScanner>();
        services.AddSingleton<IScanner, NpmGlobalScanner>();
        services.AddSingleton<IProjectLevelScanner, NpmProjectScanner>();
        services.AddSingleton<IUpdateChecker, NpmRegistryChecker>();
        return services;
    }
}
