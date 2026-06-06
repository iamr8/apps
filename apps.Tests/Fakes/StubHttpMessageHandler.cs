using System.Net;
using System.Text;

namespace apps.Tests.Fakes;

/// <summary>
/// Scripts HTTP responses for tests. Matches on the request's absolute path (e.g.
/// <c>/lodash/latest</c>) and records every request for assertions. Unmatched requests
/// return <c>404</c> so a missing stub fails loudly rather than hanging.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _byPath = new(StringComparer.Ordinal);

    /// <summary>Every request URI received, in call order.</summary>
    public List<Uri> Requests { get; } = [];

    /// <summary>Registers a JSON (200 OK) response for an exact request path.</summary>
    public StubHttpMessageHandler WithJson(string path, string json)
    {
        _byPath[path] = (HttpStatusCode.OK, json);
        return this;
    }

    /// <summary>Registers an arbitrary status (and optional body) for an exact request path.</summary>
    public StubHttpMessageHandler WithStatus(string path, HttpStatusCode status, string body = "")
    {
        _byPath[path] = (status, body);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("request has no URI");
        Requests.Add(uri);

        if (_byPath.TryGetValue(uri.AbsolutePath, out var match))
        {
            return Task.FromResult(new HttpResponseMessage(match.Status)
            {
                Content = new StringContent(match.Body, Encoding.UTF8, "application/json"),
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"no stub for {uri.AbsolutePath}", Encoding.UTF8, "text/plain"),
        });
    }
}
