using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.Docker;

/// <summary>Registers Docker platform scanner with integrated Docker Hub checker.</summary>
public static class DockerRegistration
{
    /// <summary>Adds the Docker image scanner with Docker Hub tag comparison.</summary>
    public static IServiceCollection AddDocker(this IServiceCollection services)
    {
        services.AddCheckerClient("dockerhub", "https://hub.docker.com", 6);

        services.AddSingleton<IScanner, DockerImageScanner>();
        return services;
    }
}
