using System.Net;

using apps.Components.Chrome;
using apps.Tests.Fakes;

using Microsoft.Extensions.Logging.Abstractions;

namespace apps.Tests.Components;

/// <summary>
/// Covers <see cref="ChromeExtScanner"/>: the manifest projection, the CRX update-check XML parser,
/// the update-check URL builder, and the <c>CheckAsync</c> flow against a stubbed update endpoint.
/// </summary>
public sealed class ChromeExtScannerTests
{
    private const string UpdatePath = "/service/update2/crx";

    [Test]
    public async Task ProjectManifest_ReadsTrimmedFields()
    {
        var manifest = new ChromeManifest
        {
            Name = "  uBlock Origin  ",
            Version = "  1.54.0  ",
            Description = "  An efficient blocker.  ",
        };

        var projected = ChromeExtScanner.ProjectManifest(manifest);

        await Assert.That(projected).IsNotNull();
        await Assert.That(projected!.Value.Name).IsEqualTo("uBlock Origin");
        await Assert.That(projected.Value.Version).IsEqualTo("1.54.0");
        await Assert.That(projected.Value.Description).IsEqualTo("An efficient blocker.");
    }

    [Test]
    public async Task ProjectManifest_MissingVersionAndDescription_YieldsNulls()
    {
        var projected = ChromeExtScanner.ProjectManifest(new ChromeManifest { Name = "Lonely" });

        await Assert.That(projected).IsNotNull();
        await Assert.That(projected!.Value.Name).IsEqualTo("Lonely");
        await Assert.That(projected.Value.Version).IsNull();
        await Assert.That(projected.Value.Description).IsNull();
    }

    [Test]
    public async Task ProjectManifest_NullManifest_ReturnsNull()
    {
        await Assert.That(ChromeExtScanner.ProjectManifest(null)).IsNull();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("__MSG_appName__")]
    public async Task ProjectManifest_MissingOrSyntheticName_ReturnsNull(string? name)
    {
        var manifest = new ChromeManifest { Name = name, Version = "1.0.0" };

        await Assert.That(ChromeExtScanner.ProjectManifest(manifest)).IsNull();
    }

    [Test]
    [Arguments("abcdefghijklmnopabcdefghijklmnop")]
    [Arguments("cjpalhdlnbpafiamejdnhcphjbkeiagm")]
    public async Task BuildUpdateCheckUrl_EmbedsExtensionId(string extensionId)
    {
        var url = ChromeExtScanner.BuildUpdateCheckUrl(extensionId);

        await Assert.That(url).StartsWith("/service/update2/crx?");
        await Assert.That(url).Contains($"x=id%3D{extensionId}%26uc");
    }

    [Test]
    public async Task ParseUpdateCheckVersion_ReturnsAdvertisedVersion()
    {
        var version = ChromeExtScanner.ParseUpdateCheckVersion(UpdateXml("ok", "2.0.0"));

        await Assert.That(version).IsEqualTo("2.0.0");
    }

    [Test]
    public async Task ParseUpdateCheckVersion_NoUpdateStatus_ReturnsNull()
    {
        const string xml = """
                           <?xml version="1.0" encoding="UTF-8"?>
                           <gupdate xmlns="http://www.google.com/update2/response" protocol="2.0">
                             <app appid="abc">
                               <updatecheck status="noupdate"/>
                             </app>
                           </gupdate>
                           """;

        await Assert.That(ChromeExtScanner.ParseUpdateCheckVersion(xml)).IsNull();
    }

    [Test]
    public async Task ParseUpdateCheckVersion_MissingUpdateCheckNode_ReturnsNull()
    {
        const string xml = """
                           <?xml version="1.0" encoding="UTF-8"?>
                           <gupdate xmlns="http://www.google.com/update2/response" protocol="2.0"/>
                           """;

        await Assert.That(ChromeExtScanner.ParseUpdateCheckVersion(xml)).IsNull();
    }

    [Test]
    public async Task ParseUpdateCheckVersion_MissingVersionAttribute_ReturnsNull()
    {
        await Assert.That(ChromeExtScanner.ParseUpdateCheckVersion(UpdateXml("ok", version: null))).IsNull();
    }

    [Test]
    public async Task ParseUpdateCheckVersion_MalformedXml_Throws()
    {
        await Assert.That(() => ChromeExtScanner.ParseUpdateCheckVersion("not xml"))
            .Throws<System.Xml.XmlException>();
    }

    [Test]
    public async Task CheckAsync_WhenEndpointHasNewerVersion_SetsLatestAndUpdateAvailable()
    {
        var handler = new StubHttpMessageHandler().WithStatus(UpdatePath, HttpStatusCode.OK, UpdateXml("ok", "2.0.0"));
        var scanner = CreateScanner(handler);
        var record = ExtensionRecord(scanner, "cjpalhdlnbpafiamejdnhcphjbkeiagm", installed: "1.0.0");

        var results = await Check(scanner, record);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsEqualTo("2.0.0");
        await Assert.That(record.UpdateAvailable).IsTrue();
    }

    [Test]
    public async Task CheckAsync_WhenUpToDate_NoUpdateAvailable()
    {
        var handler = new StubHttpMessageHandler().WithStatus(UpdatePath, HttpStatusCode.OK, UpdateXml("ok", "2.0.0"));
        var scanner = CreateScanner(handler);
        var record = ExtensionRecord(scanner, "cjpalhdlnbpafiamejdnhcphjbkeiagm", installed: "2.0.0");

        await Check(scanner, record);

        await Assert.That(record.App.LatestVersion).IsEqualTo("2.0.0");
        await Assert.That(record.UpdateAvailable).IsFalse();
    }

    [Test]
    public async Task CheckAsync_NoUpdateResponse_LeavesLatestUnsetWithoutError()
    {
        var handler = new StubHttpMessageHandler().WithStatus(UpdatePath, HttpStatusCode.OK, UpdateXml("noupdate", version: null));
        var scanner = CreateScanner(handler);
        var record = ExtensionRecord(scanner, "cjpalhdlnbpafiamejdnhcphjbkeiagm", installed: "1.0.0");

        var results = await Check(scanner, record);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsNull();
        await Assert.That(record.UpdateAvailable).IsFalse();
    }

    [Test]
    public async Task CheckAsync_WhenEndpointReturns404_EmitsNothingWithoutError()
    {
        var handler = new StubHttpMessageHandler().WithStatus(UpdatePath, HttpStatusCode.NotFound);
        var scanner = CreateScanner(handler);
        var record = ExtensionRecord(scanner, "cjpalhdlnbpafiamejdnhcphjbkeiagm", installed: "1.0.0");

        var results = await Check(scanner, record);

        await Assert.That(results).IsEmpty();
        await Assert.That(record.App.LatestVersion).IsNull();
    }

    [Test]
    public async Task CheckAsync_WhenPackageIdMissing_SkipsHttpAndEmitsNothing()
    {
        var handler = new StubHttpMessageHandler();
        var scanner = CreateScanner(handler);
        var record = ExtensionRecord(scanner, packageId: null, installed: "1.0.0");

        var results = await Check(scanner, record);

        await Assert.That(results).IsEmpty();
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task CheckAsync_EmptyInput_EmitsNothing()
    {
        var handler = new StubHttpMessageHandler();
        var scanner = CreateScanner(handler);

        var results = await Check(scanner);

        await Assert.That(results).IsEmpty();
        await Assert.That(handler.Requests).IsEmpty();
    }

    private static ChromeExtScanner CreateScanner(StubHttpMessageHandler handler) =>
        new(new StubHttpClientFactory(handler), NullLogger<ChromeExtScanner>.Instance);

    private static AppRecord ExtensionRecord(ChromeExtScanner scanner, string? packageId, string installed)
    {
        var app = new DiscoveredApp(scanner, "uBlock Origin", new AppIdentifier("ChromeExt", "Chrome", "Extension"), AppKind.Extension)
        {
            PackageId = packageId,
            InstalledVersion = installed,
            Attribute = AppAttribute.ChromeExtension,
            UpdateInfo = packageId,
        };
        return new AppRecord(app);
    }

    private static async Task<List<(AppRecord App, bool Error)>> Check(ChromeExtScanner scanner, params AppRecord[] records)
    {
        var results = new List<(AppRecord App, bool Error)>();
        await foreach (var item in scanner.CheckAsync(records))
        {
            results.Add(item);
        }

        return results;
    }

    private static string UpdateXml(string status, string? version)
    {
        var versionAttr = version is null ? string.Empty : $" version=\"{version}\"";
        return $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <gupdate xmlns="http://www.google.com/update2/response" protocol="2.0">
                  <app appid="cjpalhdlnbpafiamejdnhcphjbkeiagm">
                    <updatecheck status="{status}"{versionAttr}/>
                  </app>
                </gupdate>
                """;
    }
}
