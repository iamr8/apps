namespace apps;

/// <summary>
/// Pre-establishes HTTP connections to known registry hosts during the scan phase
/// so that TLS handshakes are complete before the check phase begins.
/// Each warm-up request is a lightweight HEAD/GET that forces DNS resolution,
/// TCP connection, and TLS negotiation into the connection pool.
/// </summary>
public sealed class ConnectionWarmup(IHttpClientFactory httpClientFactory)
{
    private static readonly (string ClientName, string PingPath)[] Targets =
    [
        ("nuget", "/v3/index.json"),
        ("npm", "/"),
        ("github", "/rate_limit"),
        ("goproxy", "/"),
        ("dockerhub", "/v2/repositories/library/hello-world/tags/latest")
    ];

    /// <summary>
    /// Fires lightweight requests to all known registry hosts concurrently.
    /// Failures are silently ignored — this is purely an optimization hint.
    /// </summary>
    public async Task WarmAsync(CancellationToken cancellationToken = default)
    {
        var tasks = Targets.Select(t => WarmOneAsync(t.ClientName, t.PingPath, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task WarmOneAsync(string clientName, string path, CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(clientName);
            using var request = new HttpRequestMessage(HttpMethod.Head, path);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Connection warm-up is best-effort; swallow all errors.
        }
    }
}

