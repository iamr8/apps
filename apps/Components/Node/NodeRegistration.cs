using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.Node;

/// <summary>Registers Node.js/npm platform scanner with integrated npm registry checker.</summary>
public static class NodeRegistration
{
    /// <summary>Adds the consolidated Node scanner (Node.js versions + npm global packages + registry checker).</summary>
    public static IServiceCollection AddNode(this IServiceCollection services)
    {
        services.AddCheckerClient("npm", "https://registry.npmjs.org", 32, c =>
        {
            c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });

        services.AddSingleton<IScanner, NodeScanner>();
        return services;
    }
}
