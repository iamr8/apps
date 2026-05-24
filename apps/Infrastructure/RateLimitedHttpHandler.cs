using System.Collections.Frozen;
using System.Net;
using System.Threading.RateLimiting;

namespace apps.Infrastructure;

/// <summary>
/// DelegatingHandler that enforces per-host concurrency limits and respects Retry-After.
/// Registered as the primary message handler for every named HttpClient.
///
/// Concurrency model:
///   - One SemaphoreSlim per host (static, shared across all instances) caps parallel requests.
///   - JetBrains also uses a TokenBucketRateLimiter (4 req/s sustained).
///   - On 429 / 503 with Retry-After the request waits and retries once automatically.
/// </summary>
public sealed class RateLimitedHttpHandler : DelegatingHandler
{
    // Per-host in-flight concurrency caps — CDN-backed hosts use higher limits.
    private static readonly FrozenDictionary<string, SemaphoreSlim> Slots = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase)
    {
        ["api.nuget.org"] = new(24, 24),
        ["registry.npmjs.org"] = new(32, 32),
        ["proxy.golang.org"] = new(24, 24),
        ["api.github.com"] = new(10, 10),
        ["hub.docker.com"] = new(6, 6),
        ["auth.docker.io"] = new(4, 4),
        ["plugins.jetbrains.com"] = new(4, 4),
        ["marketplace.visualstudio.com"] = new(2, 2) // batch: only 1-2 calls total
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim DefaultSlot = new(8, 8);

    // Token bucket for JetBrains (safe at 4 req/s)
    private static readonly TokenBucketRateLimiter JetbrainsLimiter = new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = 4,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        TokensPerPeriod = 4,
        AutoReplenishment = true,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 64
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri!.Host;
        var sem = Slots.GetValueOrDefault(host) ?? DefaultSlot;

        // Extra token-bucket gate for time-sensitive hosts
        if (host.Equals("plugins.jetbrains.com", StringComparison.OrdinalIgnoreCase))
        {
            using var lease = await JetbrainsLimiter.AcquireAsync(1, cancellationToken);
            if (!lease.IsAcquired)
            {
                throw new OperationCanceledException("JetBrains rate-limit queue is full.", cancellationToken);
            }
        }

        await sem.WaitAsync(cancellationToken);
        try
        {
            var response = await base.SendAsync(request, cancellationToken);

            // Honour Retry-After on 429 / 503 — single automatic retry
            if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable))
            {
                return response;
            }

            var delay = response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow ?? TimeSpan.FromSeconds(10);
            await Task.Delay(delay, cancellationToken);

            // Retry (must clone the request — HttpRequestMessage is single-use)
            using var retry = CloneRequest(request);
            return await base.SendAsync(retry, cancellationToken);
        }
        finally
        {
            sem.Release();
        }
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (original.Content is not null)
        {
            // HttpContent is single-use after sending; callers that need retry must supply
            // rewindable content. For retries triggered here the original content stream is
            // already consumed, so we attempt to read its buffered bytes. If the content was
            // already read to completion (most common case: small JSON bodies), ReadAsByteArray
            // succeeds from the internal buffer without a network round-trip.
            var bytes = original.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var newContent = new ByteArrayContent(bytes);

            if (original.Content.Headers.ContentType is not null)
            {
                newContent.Headers.ContentType = original.Content.Headers.ContentType;
            }

            clone.Content = newContent;
        }

        clone.Version = original.Version;
        return clone;
    }
}