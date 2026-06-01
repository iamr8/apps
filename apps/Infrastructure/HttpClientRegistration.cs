using System.Net;
using System.Threading.RateLimiting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

using Polly;

namespace apps.Infrastructure;

/// <summary>
/// Shared extension for registering named HttpClients with rate-limited handlers
/// and pre-configured SocketsHttpHandler settings.
/// </summary>
public static class HttpClientRegistration
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers a named HttpClient with a dedicated <see cref="SocketsHttpHandler"/> and a
        /// Polly resilience pipeline that enforces per-client concurrency, retries transient errors
        /// (honouring <c>Retry-After</c> on 429 / 503), and applies a per-attempt timeout.
        /// Forces HTTP/2 with fallback to HTTP/1.1.
        /// </summary>
        public void AddCheckerClient(
            string name,
            string baseUrl,
            int maxConnections,
            Action<HttpClient>? headers = null,
            int totalTimeoutSeconds = 15,
            int attemptTimeoutSeconds = 10)
        {
            services.AddHttpClient(name, c =>
                {
                    c.BaseAddress = new Uri(baseUrl);
                    c.Timeout = TimeSpan.FromSeconds(totalTimeoutSeconds);
                    c.DefaultRequestVersion = HttpVersion.Version20;
                    c.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                    c.DefaultRequestHeaders.UserAgent.ParseAdd($"apps/{Program.Version}");
                    headers?.Invoke(c);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    MaxConnectionsPerServer = maxConnections,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
                    EnableMultipleHttp2Connections = true,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli
                })
                .AddResilienceHandler($"checker-{name}", builder =>
                {
                    // Gate: acquire a concurrency permit before the inner handler executes;
                    // the pipeline releases it automatically on completion or cancellation.
                    // One ConcurrencyLimiter per named client — caps in-flight requests at the application
                    // level independently of the underlying TCP connection pool.
                    // QueueLimit = int.MaxValue means requests always wait for a free slot rather than
                    // throwing RateLimiterRejectedException when the queue is momentarily full.
                    builder.AddRateLimiter(new ConcurrencyLimiter(new ConcurrencyLimiterOptions
                    {
                        PermitLimit = maxConnections,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = int.MaxValue
                    }));

                    // Retry transient HTTP errors with exponential back-off + jitter.
                    // On 429 / 503, DelayGenerator reads the Retry-After header so the wait
                    // matches exactly what the server requested; other errors use the default backoff.
                    builder.AddRetry(new HttpRetryStrategyOptions
                    {
                        MaxRetryAttempts = 3,
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        Delay = TimeSpan.FromMilliseconds(200),
                        DelayGenerator = args =>
                        {
                            if (args.Outcome.Result is { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable, Headers.RetryAfter: { } retryAfter })
                            {
                                var serverDelay = retryAfter.Delta
                                                  ?? (retryAfter.Date - DateTimeOffset.UtcNow)
                                                  ?? TimeSpan.FromSeconds(10);
                                return ValueTask.FromResult<TimeSpan?>(serverDelay);
                            }

                            return ValueTask.FromResult<TimeSpan?>(null);
                        },
                        ShouldHandle = args => args.Outcome switch
                        {
                            { Exception: HttpRequestException } => PredicateResult.True(),
                            { Exception: TaskCanceledException } => PredicateResult.True(),
                            { Result.StatusCode: HttpStatusCode.RequestTimeout } => PredicateResult.True(),
                            { Result.StatusCode: HttpStatusCode.TooManyRequests } => PredicateResult.True(),
                            { Result.StatusCode: HttpStatusCode.ServiceUnavailable } => PredicateResult.True(),
                            { Result.StatusCode: >= HttpStatusCode.InternalServerError } => PredicateResult.True(),
                            _ => PredicateResult.False()
                        }
                    });
                    //
                    // // Open the circuit after 80% failures over ≥5 requests in a 30-second window;
                    // // keep it open for 30 s so the downstream host has time to recover.
                    // // 429 TooManyRequests is intentionally excluded — it is a rate signal, not a
                    // // reliability failure, and is already handled by the retry layer above.
                    // builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                    // {
                    //     SamplingDuration = TimeSpan.FromSeconds(30),
                    //     MinimumThroughput = 5,
                    //     FailureRatio = 0.8,
                    //     BreakDuration = TimeSpan.FromSeconds(30),
                    //     ShouldHandle = args => args.Outcome switch
                    //     {
                    //         { Exception: HttpRequestException } => PredicateResult.True(),
                    //         { Result.StatusCode: HttpStatusCode.ServiceUnavailable } => PredicateResult.True(),
                    //         { Result.StatusCode: >= HttpStatusCode.InternalServerError } => PredicateResult.True(),
                    //         _ => PredicateResult.False()
                    //     }
                    // });

                    // Per-attempt timeout — shorter than HttpClient.Timeout so that
                    // retries still fit within the outer deadline.
                    builder.AddTimeout(TimeSpan.FromSeconds(attemptTimeoutSeconds));
                });
        }
    }
}