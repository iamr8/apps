using System.Net;

using apps.Components.Node;
using apps.Tests.Fakes;

using Microsoft.Extensions.Logging.Abstractions;

namespace apps.Tests.Components;

/// <summary>
/// Covers <see cref="NodeScanner"/>: the npm-list JSON parser, npm registry path building,
/// and the <c>CheckAsync</c> update-check flow against a stubbed npm registry.
/// </summary>
public sealed class NodeScannerTests
{
    [Test]
    public async Task ParseGlobalPackages_ReadsNameAndVersion()
    {
        const string json = """
                            {
                              "dependencies": {
                                "npm": { "version": "10.2.0" },
                                "typescript": { "version": "5.4.5" }
                              }
                            }
                            """;
        var packages = NodeScanner.ParseGlobalPackages(json);

        await Assert.That(packages.Count).IsEqualTo(2);
        await Assert.That(packages).Contains(("npm", "10.2.0"));
        await Assert.That(packages).Contains(("typescript", "5.4.5"));
    }

    [Test]
    public async Task ParseGlobalPackages_MissingVersion_YieldsNullVersion()
    {
        const string json = """{ "dependencies": { "ghost": {} } }""";
        var packages = NodeScanner.ParseGlobalPackages(json);

        await Assert.That(packages.Count).IsEqualTo(1);
        await Assert.That(packages[0].Name).IsEqualTo("ghost");
        await Assert.That(packages[0].Version).IsNull();
    }

    [Test]
    public async Task ParseGlobalPackages_NoDependenciesProperty_ReturnsEmpty()
    {
        var packages = NodeScanner.ParseGlobalPackages("""{ "name": "root" }""");
        await Assert.That(packages).IsEmpty();
    }

    [Test]
    public async Task ParseGlobalPackages_MalformedJson_Throws()
    {
        await Assert.That(() => NodeScanner.ParseGlobalPackages("not json"))
            .Throws<System.Text.Json.JsonException>();
    }

    [Test]
    [Arguments("lodash", "/lodash/latest")]
    [Arguments("@types/node", "/@types%2Fnode/latest")]
    [Arguments("@angular/core", "/@angular%2Fcore/latest")]
    [Arguments("@noslash", "/@noslash/latest")]
    public async Task BuildRegistryPath_EncodesScopedPackages(string packageName, string expected)
    {
        await Assert.That(NodeScanner.BuildRegistryPath(packageName)).IsEqualTo(expected);
    }

    [Test]
    public async Task CheckAsync_WhenRegistryHasNewerVersion_SetsLatestAndUpdateAvailable()
    {
        var handler = new StubHttpMessageHandler().WithJson("/lodash/latest", """{ "version": "2.0.0" }""");
        var scanner = CreateScanner(handler);
        var record = NpmRecord(scanner, "lodash", installed: "1.0.0");

        var results = await Check(scanner, record);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsEqualTo("2.0.0");
        await Assert.That(record.UpdateAvailable).IsTrue();
    }

    [Test]
    public async Task CheckAsync_WhenUpToDate_NoUpdateAvailable()
    {
        var handler = new StubHttpMessageHandler().WithJson("/lodash/latest", """{ "version": "2.0.0" }""");
        var scanner = CreateScanner(handler);
        var record = NpmRecord(scanner, "lodash", installed: "2.0.0");

        await Check(scanner, record);

        await Assert.That(record.App.LatestVersion).IsEqualTo("2.0.0");
        await Assert.That(record.UpdateAvailable).IsFalse();
    }

    [Test]
    public async Task CheckAsync_WhenRegistryReturns404_LeavesLatestUnsetWithoutError()
    {
        var handler = new StubHttpMessageHandler().WithStatus("/ghost/latest", HttpStatusCode.NotFound);
        var scanner = CreateScanner(handler);
        var record = NpmRecord(scanner, "ghost", installed: "1.0.0");

        var results = await Check(scanner, record);

        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsNull();
        await Assert.That(record.UpdateAvailable).IsFalse();
    }

    [Test]
    public async Task CheckAsync_SdkRecord_PassesThroughWithoutHttpCall()
    {
        var handler = new StubHttpMessageHandler();
        var scanner = CreateScanner(handler);
        var sdk = new DiscoveredApp(scanner, "node", new AppIdentifier("Node", "Node", "Sdk"), AppKind.DevTool)
        {
            InstalledVersion = "20.0.0",
            Attribute = AppAttribute.DevTool | AppAttribute.Sdk,
        };
        var record = new AppRecord(sdk);

        var results = await Check(scanner, record);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(handler.Requests).IsEmpty();
    }

    private static NodeScanner CreateScanner(StubHttpMessageHandler handler) =>
        new(new FakeProcessRunner(), new StubHttpClientFactory(handler), NullLogger<NodeScanner>.Instance);

    private static AppRecord NpmRecord(NodeScanner scanner, string name, string installed)
    {
        var app = new DiscoveredApp(scanner, name, new AppIdentifier("Node", "npm", "Global Package"), AppKind.DevTool)
        {
            PackageId = name,
            InstalledVersion = installed,
            Attribute = AppAttribute.DevTool | AppAttribute.Library,
            UpdateInfo = name,
        };
        return new AppRecord(app);
    }

    private static async Task<List<(AppRecord App, bool Error)>> Check(NodeScanner scanner, params AppRecord[] records)
    {
        var results = new List<(AppRecord App, bool Error)>();
        await foreach (var item in scanner.CheckAsync(records))
        {
            results.Add(item);
        }

        return results;
    }
}
