using System.Net;

using apps.Components.JetBrains;
using apps.Tests.Fakes;

using Microsoft.Extensions.Logging.Abstractions;

namespace apps.Tests.Components;

/// <summary>
/// Covers <see cref="JetBrainsPluginScanner"/>: the <c>plugin.xml</c> descriptor parser and the
/// <c>CheckAsync</c> update-check flow against a stubbed JetBrains plugin repository.
/// </summary>
public sealed class JetBrainsPluginScannerTests
{
    [Test]
    public async Task ParsePluginXml_ReadsIdNameAndVersion()
    {
        const string xml = """
                           <idea-plugin>
                             <id>com.example.plugin</id>
                             <name>Example Plugin</name>
                             <version>1.2.3</version>
                           </idea-plugin>
                           """;
        var parsed = JetBrainsPluginScanner.ParsePluginXml(xml);

        await Assert.That(parsed.HasValue).IsTrue();
        await Assert.That(parsed!.Value.Id).IsEqualTo("com.example.plugin");
        await Assert.That(parsed.Value.Name).IsEqualTo("Example Plugin");
        await Assert.That(parsed.Value.Version).IsEqualTo("1.2.3");
        await Assert.That(parsed.Value.DisplayId).IsEqualTo("com.example.plugin");
    }

    [Test]
    public async Task ParsePluginXml_TrimsWhitespaceFromValues()
    {
        const string xml = """
                           <idea-plugin>
                             <id>  com.example.plugin  </id>
                             <name>
                               Example Plugin
                             </name>
                             <version>  4.5.6
                             </version>
                           </idea-plugin>
                           """;
        var parsed = JetBrainsPluginScanner.ParsePluginXml(xml);

        await Assert.That(parsed!.Value.Id).IsEqualTo("com.example.plugin");
        await Assert.That(parsed.Value.Name).IsEqualTo("Example Plugin");
        await Assert.That(parsed.Value.Version).IsEqualTo("4.5.6");
    }

    [Test]
    public async Task ParsePluginXml_MissingId_FallsBackToNameForDisplayId()
    {
        const string xml = """
                           <idea-plugin>
                             <name>Nameless Id Plugin</name>
                             <version>2.0.0</version>
                           </idea-plugin>
                           """;
        var parsed = JetBrainsPluginScanner.ParsePluginXml(xml);

        await Assert.That(parsed!.Value.Id).IsNull();
        await Assert.That(parsed.Value.Name).IsEqualTo("Nameless Id Plugin");
        await Assert.That(parsed.Value.DisplayId).IsEqualTo("Nameless Id Plugin");
    }

    [Test]
    public async Task ParsePluginXml_MissingVersion_YieldsNullVersion()
    {
        const string xml = """
                           <idea-plugin>
                             <id>com.example.plugin</id>
                           </idea-plugin>
                           """;
        var parsed = JetBrainsPluginScanner.ParsePluginXml(xml);

        await Assert.That(parsed!.Value.Version).IsNull();
        await Assert.That(parsed.Value.DisplayId).IsEqualTo("com.example.plugin");
    }

    [Test]
    public async Task ParsePluginXml_NeitherIdNorName_ReturnsNull()
    {
        const string xml = """
                           <idea-plugin>
                             <version>1.0.0</version>
                           </idea-plugin>
                           """;
        var parsed = JetBrainsPluginScanner.ParsePluginXml(xml);

        await Assert.That(parsed.HasValue).IsFalse();
    }

    [Test]
    public async Task ParsePluginXml_MalformedXml_Throws()
    {
        await Assert.That(() => JetBrainsPluginScanner.ParsePluginXml("not xml <"))
            .Throws<System.Xml.XmlException>();
    }

    [Test]
    public async Task CheckAsync_NumericId_WhenRepoHasNewerVersion_SetsLatestAndUpdateAvailable()
    {
        var handler = new StubHttpMessageHandler()
            .WithJson("/api/plugins/1234/updates", """[ { "version": "2.0.0" } ]""");
        var scanner = CreateScanner(handler);
        var record = PluginRecord(scanner, name: "Example", xmlId: "1234", installed: "1.0.0");

        var results = await Check(scanner, record);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsEqualTo("2.0.0");
        await Assert.That(record.UpdateAvailable).IsTrue();
    }

    [Test]
    public async Task CheckAsync_NumericId_DoesNotCallSearchEndpoint()
    {
        var handler = new StubHttpMessageHandler()
            .WithJson("/api/plugins/1234/updates", """[ { "version": "2.0.0" } ]""");
        var scanner = CreateScanner(handler);
        var record = PluginRecord(scanner, name: "Example", xmlId: "1234", installed: "1.0.0");

        await Check(scanner, record);

        await Assert.That(handler.Requests.Count).IsEqualTo(1);
        await Assert.That(handler.Requests[0].AbsolutePath).IsEqualTo("/api/plugins/1234/updates");
    }

    [Test]
    public async Task CheckAsync_WhenUpToDate_NoUpdateAvailable()
    {
        var handler = new StubHttpMessageHandler()
            .WithJson("/api/plugins/1234/updates", """[ { "version": "2.0.0" } ]""");
        var scanner = CreateScanner(handler);
        var record = PluginRecord(scanner, name: "Example", xmlId: "1234", installed: "2.0.0");

        await Check(scanner, record);

        await Assert.That(record.App.LatestVersion).IsEqualTo("2.0.0");
        await Assert.That(record.UpdateAvailable).IsFalse();
    }

    [Test]
    public async Task CheckAsync_XmlId_ResolvesNumericIdThenFetchesUpdates()
    {
        var handler = new StubHttpMessageHandler()
            .WithJson("/api/plugins", """[ { "id": 1234 } ]""")
            .WithJson("/api/plugins/1234/updates", """[ { "version": "3.0.0" } ]""");
        var scanner = CreateScanner(handler);
        var record = PluginRecord(scanner, name: "Example", xmlId: "com.example.plugin", installed: "1.0.0");

        var results = await Check(scanner, record);

        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsEqualTo("3.0.0");
        await Assert.That(record.UpdateAvailable).IsTrue();
    }

    [Test]
    public async Task CheckAsync_XmlId_NotFoundInSearch_LeavesLatestUnsetWithoutError()
    {
        var handler = new StubHttpMessageHandler()
            .WithJson("/api/plugins", "[ ]");
        var scanner = CreateScanner(handler);
        var record = PluginRecord(scanner, name: "Example", xmlId: "com.example.plugin", installed: "1.0.0");

        var results = await Check(scanner, record);

        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsNull();
        await Assert.That(record.UpdateAvailable).IsFalse();
    }

    [Test]
    public async Task CheckAsync_XmlId_SearchReturns404_LeavesLatestUnsetWithoutError()
    {
        var handler = new StubHttpMessageHandler()
            .WithStatus("/api/plugins", HttpStatusCode.NotFound);
        var scanner = CreateScanner(handler);
        var record = PluginRecord(scanner, name: "Example", xmlId: "com.example.plugin", installed: "1.0.0");

        var results = await Check(scanner, record);

        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsNull();
    }

    [Test]
    public async Task CheckAsync_WhenUpdatesEndpointFails_ReportsError()
    {
        var handler = new StubHttpMessageHandler()
            .WithStatus("/api/plugins/1234/updates", HttpStatusCode.InternalServerError);
        var scanner = CreateScanner(handler);
        var record = PluginRecord(scanner, name: "Example", xmlId: "1234", installed: "1.0.0");

        var results = await Check(scanner, record);

        await Assert.That(results[0].Error).IsTrue();
        await Assert.That(record.App.LatestVersion).IsNull();
    }

    [Test]
    public async Task CheckAsync_MissingUpdateInfo_PassesThroughWithoutHttpCall()
    {
        var handler = new StubHttpMessageHandler();
        var scanner = CreateScanner(handler);
        var app = new DiscoveredApp(scanner, "Example", new AppIdentifier("JetBrains", "JetBrains", "Plugin"), AppKind.Extension)
        {
            InstalledVersion = "1.0.0",
            Attribute = AppAttribute.JetBrainsPlugin,
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
        var handler = new StubHttpMessageHandler();
        var scanner = CreateScanner(handler);

        var results = await Check(scanner);

        await Assert.That(results).IsEmpty();
        await Assert.That(handler.Requests).IsEmpty();
    }

    private static JetBrainsPluginScanner CreateScanner(StubHttpMessageHandler handler) =>
        new(new StubHttpClientFactory(handler), NullLogger<JetBrainsPluginScanner>.Instance);

    private static AppRecord PluginRecord(
        JetBrainsPluginScanner scanner,
        string name,
        string xmlId,
        string installed)
    {
        var app = new DiscoveredApp(scanner, name, new AppIdentifier("JetBrains", "JetBrains", "Plugin"), AppKind.Extension)
        {
            PackageId = xmlId,
            InstalledVersion = installed,
            Attribute = AppAttribute.JetBrainsPlugin,
            UpdateInfo = xmlId,
        };
        return new AppRecord(app);
    }

    private static async Task<List<(AppRecord App, bool Error)>> Check(
        JetBrainsPluginScanner scanner,
        params AppRecord[] records)
    {
        var results = new List<(AppRecord App, bool Error)>();
        await foreach (var item in scanner.CheckAsync(records))
        {
            results.Add(item);
        }

        return results;
    }
}
