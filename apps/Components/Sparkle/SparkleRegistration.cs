using apps.Checkers;
using apps.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Components.Sparkle;

/// <summary>Registers Sparkle appcast checker (no dedicated scanner — used for apps with SUFeedURL).</summary>
public static class SparkleRegistration
{
    /// <summary>Adds the Sparkle appcast checker and its HTTP client.</summary>
    public static IServiceCollection AddSparkle(this IServiceCollection services)
    {
        services.AddCheckerClient("sparkle", "https://example.com", 8, c =>
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Sparkle/2.0"));

        services.AddSingleton<IUpdateChecker, SparkleChecker>();
        return services;
    }
}
