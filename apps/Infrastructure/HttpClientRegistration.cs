using System.Net;
using System.Net.Http;

using Microsoft.Extensions.DependencyInjection;

namespace apps.Infrastructure;

/// <summary>
/// Shared extension for registering named HttpClients with rate-limited handlers
/// and pre-configured SocketsHttpHandler settings.
/// </summary>
public static class HttpClientRegistration
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers a named HttpClient with a dedicated SocketsHttpHandler
        /// and the global RateLimitedHttpHandler as a delegating handler.
        /// Forces HTTP/2 with fallback to HTTP/1.1 and sets a 15-second timeout.
        /// </summary>
        public void AddCheckerClient(
            string name,
            string baseUrl,
            int maxConn,
            Action<HttpClient>? headers = null)
        {
            services.AddHttpClient(name, c =>
                {
                    c.BaseAddress = new Uri(baseUrl);
                    c.Timeout = DefaultTimeout;
                    c.DefaultRequestVersion = HttpVersion.Version20;
                    c.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                    headers?.Invoke(c);
                })
                .AddHttpMessageHandler<RateLimitedHttpHandler>()
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    MaxConnectionsPerServer = maxConn,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
                    EnableMultipleHttp2Connections = true,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli
                });
        }
    }
}

