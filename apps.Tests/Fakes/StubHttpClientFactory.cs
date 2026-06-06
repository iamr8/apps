namespace apps.Tests.Fakes;

/// <summary>
/// <see cref="IHttpClientFactory"/> that hands out clients backed by a single
/// <see cref="StubHttpMessageHandler"/>. Every named client shares the same base address and
/// handler, so a test can register responses once and assert against all requests made.
/// </summary>
public sealed class StubHttpClientFactory(StubHttpMessageHandler handler, string baseAddress = "https://stub.test")
    : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(baseAddress),
        };
}
