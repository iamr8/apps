using System.Net;

using apps.Components.Go;
using apps.Tests.Fakes;

using Microsoft.Extensions.Logging.Abstractions;

namespace apps.Tests.Components;

/// <summary>
/// Covers <see cref="GoScanner"/>: the <c>go version</c> parser, the <c>go version -m</c>
/// build-info parser, and the <c>CheckAsync</c> update-check flow against a stubbed Go module
/// proxy and the go.dev release feed.
/// </summary>
public sealed class GoScannerTests
{
    [Test]
    public async Task ParseSdkVersion_ReadsVersionFromGoVersionOutput()
    {
        const string output = "go version go1.22.4 darwin/arm64";
        await Assert.That(GoScanner.ParseSdkVersion(output)).IsEqualTo("1.22.4");
    }

    [Test]
    public async Task ParseSdkVersion_HandlesLinuxOutput()
    {
        const string output = "go version go1.21.0 linux/amd64\n";
        await Assert.That(GoScanner.ParseSdkVersion(output)).IsEqualTo("1.21.0");
    }

    [Test]
    public async Task ParseModuleInfo_ReadsPathAndModVersion()
    {
        const string output = "/Users/me/go/bin/golangci-lint: go1.22.3\n" +
                              "\tpath\tgithub.com/golangci/golangci-lint/cmd/golangci-lint\n" +
                              "\tmod\tgithub.com/golangci/golangci-lint\tv1.59.1\th1:abc=\n";
        var (modulePath, moduleVersion) = GoScanner.ParseModuleInfo(output);

        await Assert.That(modulePath).IsEqualTo("github.com/golangci/golangci-lint/cmd/golangci-lint");
        await Assert.That(moduleVersion).IsEqualTo("1.59.1");
    }

    [Test]
    public async Task ParseModuleInfo_NoModLine_LeavesVersionNull()
    {
        const string output = "/Users/me/go/bin/tool: go1.22.3\n\tpath\texample.com/tool\n";
        var (modulePath, moduleVersion) = GoScanner.ParseModuleInfo(output);

        await Assert.That(modulePath).IsEqualTo("example.com/tool");
        await Assert.That(moduleVersion).IsNull();
    }

    [Test]
    public async Task ParseModuleInfo_ModLineWithTooFewParts_LeavesVersionNull()
    {
        const string output = "\tmod\texample.com/tool\n";
        var (modulePath, moduleVersion) = GoScanner.ParseModuleInfo(output);

        await Assert.That(modulePath).IsNull();
        await Assert.That(moduleVersion).IsNull();
    }

    [Test]
    public async Task ParseModuleInfo_EmptyOutput_ReturnsBothNull()
    {
        var (modulePath, moduleVersion) = GoScanner.ParseModuleInfo(string.Empty);

        await Assert.That(modulePath).IsNull();
        await Assert.That(moduleVersion).IsNull();
    }

    [Test]
    public async Task CheckAsync_WhenProxyHasNewerVersion_SetsLatestAndUpdateAvailable()
    {
        var handler = new StubHttpMessageHandler()
            .WithJson("/example.com/tool/@latest", """{ "Version": "v2.0.0" }""");
        var scanner = CreateScanner(handler);
        var record = ModuleRecord(scanner, "tool", "example.com/tool", installed: "1.0.0");

        var results = await Check(scanner, record);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsEqualTo("2.0.0");
        await Assert.That(record.UpdateAvailable).IsTrue();
    }

    [Test]
    public async Task CheckAsync_WhenModuleUpToDate_NoUpdateAvailable()
    {
        var handler = new StubHttpMessageHandler()
            .WithJson("/example.com/tool/@latest", """{ "Version": "v2.0.0" }""");
        var scanner = CreateScanner(handler);
        var record = ModuleRecord(scanner, "tool", "example.com/tool", installed: "2.0.0");

        await Check(scanner, record);

        await Assert.That(record.App.LatestVersion).IsEqualTo("2.0.0");
        await Assert.That(record.UpdateAvailable).IsFalse();
    }

    [Test]
    public async Task CheckAsync_WhenProxyWalksUpPathOn404_FindsModuleRoot()
    {
        var handler = new StubHttpMessageHandler()
            .WithStatus("/github.com/owner/repo/cmd/tool/@latest", HttpStatusCode.NotFound)
            .WithJson("/github.com/owner/repo/cmd/@latest", """{ "Version": "v3.1.0" }""");
        var scanner = CreateScanner(handler);
        var record = ModuleRecord(scanner, "tool", "github.com/owner/repo/cmd/tool", installed: "3.0.0");

        var results = await Check(scanner, record);

        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsEqualTo("3.1.0");
    }

    [Test]
    public async Task CheckAsync_WhenProxyReturns404Throughout_LeavesLatestUnsetWithoutError()
    {
        var handler = new StubHttpMessageHandler();
        var scanner = CreateScanner(handler);
        var record = ModuleRecord(scanner, "ghost", "example.com/ghost", installed: "1.0.0");

        var results = await Check(scanner, record);

        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsNull();
        await Assert.That(record.UpdateAvailable).IsFalse();
    }

    [Test]
    public async Task CheckAsync_WhenProxyReturnsServerError_FlagsError()
    {
        var handler = new StubHttpMessageHandler()
            .WithStatus("/example.com/tool/@latest", HttpStatusCode.InternalServerError);
        var scanner = CreateScanner(handler);
        var record = ModuleRecord(scanner, "tool", "example.com/tool", installed: "1.0.0");

        var results = await Check(scanner, record);

        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsNull();
    }

    [Test]
    public async Task CheckAsync_SdkRecord_UsesGoDevFeedForLatestVersion()
    {
        var handler = new StubHttpMessageHandler()
            .WithJson("/dl/", """[{ "version": "go1.22.4", "stable": true }, { "version": "go1.23rc1", "stable": false }]""");
        var scanner = CreateScanner(handler);
        var record = SdkRecord(scanner, installed: "1.22.0");

        var results = await Check(scanner, record);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsEqualTo("1.22.4");
        await Assert.That(record.UpdateAvailable).IsTrue();
    }

    [Test]
    public async Task CheckAsync_SdkRecord_WhenFeedFails_FlagsErrorAndLeavesLatestUnset()
    {
        var handler = new StubHttpMessageHandler()
            .WithStatus("/dl/", HttpStatusCode.InternalServerError);
        var scanner = CreateScanner(handler);
        var record = SdkRecord(scanner, installed: "1.22.0");

        var results = await Check(scanner, record);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsTrue();
        await Assert.That(record.App.LatestVersion).IsNull();
    }

    [Test]
    public async Task CheckAsync_ToolRecordWithoutUpdateInfo_PassesThroughWithoutProxyCall()
    {
        var handler = new StubHttpMessageHandler();
        var scanner = CreateScanner(handler);
        var app = new DiscoveredApp(scanner, "tool", new AppIdentifier("Go", "Go", "Tool"), AppKind.DevTool)
        {
            Path = "/Users/me/go/bin/tool",
            Attribute = AppAttribute.DevTool,
        };
        var record = new AppRecord(app);

        var results = await Check(scanner, record);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(handler.Requests.Any(uri => uri.AbsolutePath.EndsWith("/@latest", StringComparison.Ordinal))).IsFalse();
    }

    private static GoScanner CreateScanner(StubHttpMessageHandler handler) =>
        new(new FakeProcessRunner(), new StubHttpClientFactory(handler), NullLogger<GoScanner>.Instance);

    private static AppRecord ModuleRecord(
        GoScanner scanner,
        string name,
        string modulePath,
        string installed)
    {
        var app = new DiscoveredApp(scanner, name, new AppIdentifier("Go", "Go", "Module"), AppKind.DevTool)
        {
            InstalledVersion = installed,
            Attribute = AppAttribute.DevTool | AppAttribute.Library,
            UpdateInfo = modulePath,
        };
        return new AppRecord(app);
    }

    private static AppRecord SdkRecord(GoScanner scanner, string installed)
    {
        var app = new DiscoveredApp(scanner, "go", new AppIdentifier("Go", "Go", "Sdk"), AppKind.DevTool)
        {
            InstalledVersion = installed,
            Attribute = AppAttribute.DevTool | AppAttribute.Sdk,
        };
        return new AppRecord(app);
    }

    private static async Task<List<(AppRecord App, bool Error)>> Check(GoScanner scanner, params AppRecord[] records)
    {
        var results = new List<(AppRecord App, bool Error)>();
        await foreach (var item in scanner.CheckAsync(records))
        {
            results.Add(item);
        }

        return results;
    }
}
