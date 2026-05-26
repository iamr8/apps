using apps.Checkers;
using apps.Infrastructure;
using apps.Scanners;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.Docker;

/// <summary>Registers Docker platform scanner and checker.</summary>
public static class DockerRegistration
{
    /// <summary>Adds the Docker image scanner and Docker Hub tag checker.</summary>
    public static IServiceCollection AddDocker(this IServiceCollection services)
    {
        services.AddCheckerClient("dockerhub", "https://hub.docker.com", 6);

        services.AddSingleton<IScanner, DockerImageScanner>();
        services.AddSingleton<IUpdateChecker, DockerHubChecker>();
        return services;
    }
}
