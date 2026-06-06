using System.Net;

using apps.Components.Docker;
using apps.Tests.Fakes;

using Microsoft.Extensions.Logging.Abstractions;

namespace apps.Tests.Components;

/// <summary>
/// Covers <see cref="DockerImageScanner"/>: image-reference parsing into Docker Hub
/// namespace/repository/tag, and the <c>CheckAsync</c> digest-comparison flow against a
/// stubbed Docker Hub registry.
/// </summary>
public sealed class DockerImageScannerTests
{
    private const string LocalDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string RemoteDigest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    [Test]
    [Arguments("nginx", "library", "nginx", "latest")]
    [Arguments("nginx:1.27", "library", "nginx", "1.27")]
    [Arguments("grafana/grafana", "grafana", "grafana", "latest")]
    [Arguments("grafana/grafana:10.4.0", "grafana", "grafana", "10.4.0")]
    [Arguments("library/redis:7", "library", "redis", "7")]
    public async Task TryParseImageRef_SplitsNamespaceRepoAndTag(
        string imageRef,
        string expectedNs,
        string expectedRepo,
        string expectedTag)
    {
        var ok = DockerImageScanner.TryParseImageRef(imageRef, out var ns, out var repo, out var tag);

        await Assert.That(ok).IsTrue();
        await Assert.That(ns).IsEqualTo(expectedNs);
        await Assert.That(repo).IsEqualTo(expectedRepo);
        await Assert.That(tag).IsEqualTo(expectedTag);
    }

    [Test]
    [Arguments("ghcr.io/owner/app:1.0")]
    [Arguments("registry.example.com/team/app")]
    [Arguments("localhost:5000/app:dev")]
    public async Task TryParseImageRef_PrivateRegistryHost_ReturnsFalse(string imageRef)
    {
        var ok = DockerImageScanner.TryParseImageRef(imageRef, out var ns, out var repo, out var tag);

        await Assert.That(ok).IsFalse();
        await Assert.That(ns).IsEqualTo(string.Empty);
        await Assert.That(repo).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task TryParseImageRef_EmptyInput_ReturnsFalse()
    {
        var ok = DockerImageScanner.TryParseImageRef(string.Empty, out _, out var repo, out var tag);

        await Assert.That(ok).IsFalse();
        await Assert.That(repo).IsEqualTo(string.Empty);
        await Assert.That(tag).IsEqualTo("latest");
    }

    [Test]
    public async Task CheckAsync_WhenRemoteDigestDiffers_SetsLatestVersion()
    {
        var handler = new StubHttpMessageHandler()
            .WithJson("/v2/repositories/library/nginx/tags/latest", $$"""{ "name": "latest", "digest": "{{RemoteDigest}}" }""");
        var scanner = CreateScanner(handler);
        var record = ImageRecord(scanner, imageRef: "nginx:latest", localDigest: LocalDigest);

        var results = await Check(scanner, record);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsEqualTo(RemoteDigest);
    }

    [Test]
    public async Task CheckAsync_WhenRemoteDigestMatchesLocal_LeavesLatestUnset()
    {
        var handler = new StubHttpMessageHandler()
            .WithJson("/v2/repositories/library/nginx/tags/latest", $$"""{ "name": "latest", "digest": "{{LocalDigest}}" }""");
        var scanner = CreateScanner(handler);
        var record = ImageRecord(scanner, imageRef: "nginx:latest", localDigest: LocalDigest);

        var results = await Check(scanner, record);

        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsNull();
    }

    [Test]
    public async Task CheckAsync_WhenManifestDigestMissing_FallsBackToArchImageDigest()
    {
        var handler = new StubHttpMessageHandler()
            .WithJson(
                "/v2/repositories/library/nginx/tags/latest",
                $$"""
                  {
                    "name": "latest",
                    "images": [
                      { "os": "linux", "architecture": "amd64", "digest": "{{RemoteDigest}}" },
                      { "os": "linux", "architecture": "arm64", "digest": "{{RemoteDigest}}" }
                    ]
                  }
                  """);
        var scanner = CreateScanner(handler);
        var record = ImageRecord(scanner, imageRef: "nginx:latest", localDigest: LocalDigest);

        var results = await Check(scanner, record);

        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsEqualTo(RemoteDigest);
    }

    [Test]
    public async Task CheckAsync_WhenNoRemoteDigest_LeavesLatestUnsetWithoutError()
    {
        var handler = new StubHttpMessageHandler()
            .WithJson("/v2/repositories/library/nginx/tags/latest", """{ "name": "latest" }""");
        var scanner = CreateScanner(handler);
        var record = ImageRecord(scanner, imageRef: "nginx:latest", localDigest: LocalDigest);

        var results = await Check(scanner, record);

        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsNull();
    }

    [Test]
    public async Task CheckAsync_WhenRegistryReturns404_ReportsError()
    {
        var handler = new StubHttpMessageHandler()
            .WithStatus("/v2/repositories/library/ghost/tags/latest", HttpStatusCode.NotFound);
        var scanner = CreateScanner(handler);
        var record = ImageRecord(scanner, imageRef: "ghost:latest", localDigest: LocalDigest);

        var results = await Check(scanner, record);

        await Assert.That(results[0].Error).IsTrue();
        await Assert.That(record.App.LatestVersion).IsNull();
    }

    [Test]
    public async Task CheckAsync_PrivateRegistryImage_PassesThroughWithoutHttpCall()
    {
        var handler = new StubHttpMessageHandler();
        var scanner = CreateScanner(handler);
        var record = ImageRecord(scanner, imageRef: "ghcr.io/owner/app:1.0", localDigest: LocalDigest);

        var results = await Check(scanner, record);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(handler.Requests).IsEmpty();
        await Assert.That(record.App.LatestVersion).IsNull();
    }

    [Test]
    public async Task CheckAsync_WithoutUpdateInfo_PassesThroughWithoutHttpCall()
    {
        var handler = new StubHttpMessageHandler();
        var scanner = CreateScanner(handler);
        var app = new DiscoveredApp(scanner, "nginx:latest", new AppIdentifier("Docker", "Docker", "Image"), AppKind.DevTool)
        {
            InstalledVersion = "latest",
            Digest = LocalDigest,
            Attribute = AppAttribute.DevTool | AppAttribute.Image,
            UpdateInfo = null,
        };
        var record = new AppRecord(app);

        var results = await Check(scanner, record);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task CheckAsync_EmptyInput_YieldsNothing()
    {
        var scanner = CreateScanner(new StubHttpMessageHandler());

        var results = await Check(scanner);

        await Assert.That(results).IsEmpty();
    }

    private static DockerImageScanner CreateScanner(StubHttpMessageHandler handler) =>
        new(new FakeProcessRunner(), new StubHttpClientFactory(handler), NullLogger<DockerImageScanner>.Instance);

    private static AppRecord ImageRecord(DockerImageScanner scanner, string imageRef, string localDigest)
    {
        var app = new DiscoveredApp(scanner, imageRef, new AppIdentifier("Docker", "Docker", "Image"), AppKind.DevTool)
        {
            InstalledVersion = imageRef.Contains(':') ? imageRef[(imageRef.LastIndexOf(':') + 1)..] : "latest",
            Digest = localDigest,
            Attribute = AppAttribute.DevTool | AppAttribute.Image,
            UpdateInfo = imageRef,
        };
        return new AppRecord(app);
    }

    private static async Task<List<(AppRecord App, bool Error)>> Check(DockerImageScanner scanner, params AppRecord[] records)
    {
        var results = new List<(AppRecord App, bool Error)>();
        await foreach (var item in scanner.CheckAsync(records))
        {
            results.Add(item);
        }

        return results;
    }
}
