using System.Net;

using apps.Components.MacOs;
using apps.Tests.Fakes;

using Microsoft.Extensions.Logging.Abstractions;

namespace apps.Tests.Components;

/// <summary>
/// Covers <see cref="MacApplicationsScanner"/>: the pure parsing/normalization seams
/// (<c>softwareupdate</c> output, App Store page version extraction, Homebrew token derivation,
/// plist-string normalization, <c>brew info</c> JSON parsing) and the <c>CheckAsync</c>
/// update-check flow against stubbed iTunes, Sparkle, and Homebrew endpoints.
/// </summary>
public sealed class MacApplicationsScannerTests
{
    [Test]
    public async Task ParseSoftwareUpdates_ReadsLabelAndVersion()
    {
        const string output = """
                              Software Update Tool

                              Finding available software
                              * Label: macOS Sequoia 15.5-24F74
                                  Title: macOS Sequoia 15.5, Version: 15.5, Size: 3000000KiB, Recommended: YES, Action: restart,
                              * Label: Safari18.5MontereyAuto-18.5
                                  Title: Safari, Version: 18.5, Size: 100000KiB, Recommended: YES,
                              """;
        var entries = MacApplicationsScanner.ParseSoftwareUpdates(output);

        await Assert.That(entries.Count).IsEqualTo(2);
        await Assert.That(entries[0]).IsEqualTo(("macOS Sequoia 15.5-24F74", (string?)"15.5"));
        await Assert.That(entries[1]).IsEqualTo(("Safari18.5MontereyAuto-18.5", (string?)"18.5"));
    }

    [Test]
    public async Task ParseSoftwareUpdates_LabelWithoutVersionLine_YieldsNullVersion()
    {
        const string output = """
                              * Label: SomeUpdate-1.0
                                  Title: Some Update, Recommended: YES,
                              """;
        var entries = MacApplicationsScanner.ParseSoftwareUpdates(output);

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].Label).IsEqualTo("SomeUpdate-1.0");
        await Assert.That(entries[0].Version).IsNull();
    }

    [Test]
    public async Task ParseSoftwareUpdates_NoUpdates_ReturnsEmpty()
    {
        const string output = """
                              Software Update Tool

                              No new software available.
                              """;
        var entries = MacApplicationsScanner.ParseSoftwareUpdates(output);
        await Assert.That(entries).IsEmpty();
    }

    [Test]
    [Arguments("Version: 15.5, Size: 100KiB", "15.5")]
    [Arguments("Version: 18.5", "18.5")]
    [Arguments("    Title: X, Version: 2.0, Action: restart", "2.0")]
    [Arguments("no marker here", null)]
    public async Task ExtractVersionFromSoftwareUpdateLine_ReadsUpToComma(string line, string? expected)
    {
        await Assert.That(MacApplicationsScanner.ExtractVersionFromSoftwareUpdateLine(line)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("Visual Studio Code", "visual-studio-code")]
    [Arguments("1Password", "1password")]
    [Arguments("Google Chrome", "google-chrome")]
    [Arguments("Foo_Bar", "foo-bar")]
    [Arguments("Trailing Space ", "trailing-space")]
    public async Task CreateToken_NormalizesToHomebrewStyle(string appName, string expected)
    {
        await Assert.That(MacApplicationsScanner.CreateToken(appName)).IsEqualTo(expected);
    }

    [Test]
    public async Task Normalize_TrimsAndStripsLeftToRightMark()
    {
        await Assert.That(MacApplicationsScanner.Normalize("  Pages‎  ")).IsEqualTo("Pages");
    }

    [Test]
    public async Task Normalize_Null_ReturnsNull()
    {
        await Assert.That(MacApplicationsScanner.Normalize(null)).IsNull();
    }

    [Test]
    public async Task ExtractMostRecentVersion_ReadsPrimarySubtitleVersion()
    {
        const string html = """
                            <html><body>
                            <script type="application/json" id="serialized-server-data">[{"data":{"primarySubtitle":"Version 4.2.1"}}]</script>
                            </body></html>
                            """;
        await Assert.That(MacApplicationsScanner.ExtractMostRecentVersion(html)).IsEqualTo("4.2.1");
    }

    [Test]
    public async Task ExtractMostRecentVersion_NoServerDataBlob_ReturnsNull()
    {
        await Assert.That(MacApplicationsScanner.ExtractMostRecentVersion("<html></html>")).IsNull();
    }

    [Test]
    public async Task ExtractMostRecentVersion_NoVersionInSubtitle_ReturnsNull()
    {
        const string html = """<script id="serialized-server-data">{"primarySubtitle":"Bug fixes"}</script>""";
        await Assert.That(MacApplicationsScanner.ExtractMostRecentVersion(html)).IsNull();
    }

    [Test]
    public async Task ParseBrewInfo_ReadsFormulaeAndCasks()
    {
        const string json = """
                            {
                              "formulae": [
                                {
                                  "name": "wget",
                                  "full_name": "wget",
                                  "desc": "Internet file retriever",
                                  "versions": { "stable": "1.25.0" },
                                  "installed": [ { "version": "1.24.5" } ]
                                }
                              ],
                              "casks": [
                                {
                                  "token": "chatgpt",
                                  "name": [ "ChatGPT" ],
                                  "desc": "OpenAI desktop app",
                                  "version": "1.2.0",
                                  "installed": "1.1.0"
                                }
                              ]
                            }
                            """;
        var info = MacApplicationsScanner.ParseBrewInfo(json, success: true);

        await Assert.That(info).IsNotNull();
        await Assert.That(info!.Formulae.Length).IsEqualTo(1);
        await Assert.That(info.Formulae[0].Name).IsEqualTo("wget");
        await Assert.That(info.Formulae[0].LatestVersion.StableVersion).IsEqualTo("1.25.0");
        await Assert.That(info.Formulae[0].IsOutdated).IsTrue();
        await Assert.That(info.Casks.Length).IsEqualTo(1);
        await Assert.That(info.Casks[0].Token).IsEqualTo("chatgpt");
        await Assert.That(info.Casks[0].LatestVersion).IsEqualTo("1.2.0");
    }

    [Test]
    public async Task ParseBrewInfo_CommandFailed_ReturnsNull()
    {
        await Assert.That(MacApplicationsScanner.ParseBrewInfo("{}", success: false)).IsNull();
    }

    [Test]
    public async Task ParseBrewInfo_EmptyOutput_ReturnsNull()
    {
        await Assert.That(MacApplicationsScanner.ParseBrewInfo("   ", success: true)).IsNull();
    }

    [Test]
    public async Task ParseBrewInfo_MalformedJson_Throws()
    {
        await Assert.That(() => MacApplicationsScanner.ParseBrewInfo("not json", success: true))
            .Throws<System.Text.Json.JsonException>();
    }

    [Test]
    public async Task CheckAsync_AppStoreApp_NewerVersion_SetsLatestAndUpdateAvailable()
    {
        var handler = new StubHttpMessageHandler()
            .WithJson("/lookup", """{ "resultCount": 1, "results": [ { "kind": "mac-software", "version": "3.0.0" } ] }""");
        var scanner = CreateScanner(handler);
        var record = AppStoreRecord(scanner, "Numbers", bundleId: "com.apple.iWork.Numbers", installed: "2.0.0");

        var results = await Check(scanner, record);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsEqualTo("3.0.0");
        await Assert.That(record.UpdateAvailable).IsTrue();
    }

    [Test]
    public async Task CheckAsync_AppStoreApp_UpToDate_NoUpdateAvailable()
    {
        var handler = new StubHttpMessageHandler()
            .WithJson("/lookup", """{ "resultCount": 1, "results": [ { "kind": "mac-software", "version": "2.0.0" } ] }""");
        var scanner = CreateScanner(handler);
        var record = AppStoreRecord(scanner, "Numbers", bundleId: "com.apple.iWork.Numbers", installed: "2.0.0");

        await Check(scanner, record);

        await Assert.That(record.App.LatestVersion).IsEqualTo("2.0.0");
        await Assert.That(record.UpdateAvailable).IsFalse();
    }

    [Test]
    public async Task CheckAsync_AppStoreApp_NoResults_LeavesLatestUnset()
    {
        var handler = new StubHttpMessageHandler()
            .WithJson("/lookup", """{ "resultCount": 0, "results": [] }""");
        var scanner = CreateScanner(handler);
        var record = AppStoreRecord(scanner, "Ghost", bundleId: "com.ghost.app", installed: "1.0.0");

        var results = await Check(scanner, record);

        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsNull();
        await Assert.That(record.UpdateAvailable).IsFalse();
    }

    [Test]
    public async Task CheckAsync_SparkleFeed_NewerVersion_SetsLatestAndBuild()
    {
        const string feed = """
                            <?xml version="1.0"?>
                            <rss xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
                              <channel>
                                <item>
                                  <enclosure url="https://x/y.zip" sparkle:shortVersionString="5.1" sparkle:version="510" />
                                </item>
                              </channel>
                            </rss>
                            """;
        var handler = new StubHttpMessageHandler().WithJson("/appcast.xml", feed);
        var scanner = CreateScanner(handler);
        var record = SparkleRecord(scanner, "Widget", "https://stub.test/appcast.xml", installed: "5.0");

        var results = await Check(scanner, record);

        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsEqualTo("5.1");
        await Assert.That(record.App.LatestBuildNumber).IsEqualTo("510");
        await Assert.That(record.UpdateAvailable).IsTrue();
    }

    [Test]
    public async Task CheckAsync_SparkleFeed_HttpError_ReportsError()
    {
        var handler = new StubHttpMessageHandler().WithStatus("/appcast.xml", HttpStatusCode.InternalServerError);
        var scanner = CreateScanner(handler);
        var record = SparkleRecord(scanner, "Widget", "https://stub.test/appcast.xml", installed: "5.0");

        var results = await Check(scanner, record);

        await Assert.That(results.Any(r => r.App == record && r.Error)).IsTrue();
        await Assert.That(record.App.LatestVersion).IsNull();
    }

    [Test]
    public async Task CheckAsync_HomebrewCask_MatchesArtifactPath_SetsLatest()
    {
        const string caskJson = """
                               {
                                 "token": "chatgpt",
                                 "name": [ "ChatGPT" ],
                                 "desc": "OpenAI app",
                                 "version": "1.5.0",
                                 "artifacts": [ { "app": [ "ChatGPT.app" ], "target": "/Applications/ChatGPT.app" } ]
                               }
                               """;
        var handler = new StubHttpMessageHandler().WithJson("/api/cask/chatgpt.json", caskJson);
        var scanner = CreateScanner(handler);
        var record = CaskRecord(scanner, "ChatGPT", path: "/Applications/ChatGPT.app", installed: "1.4.0");

        var results = await Check(scanner, record);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsEqualTo("1.5.0");
        await Assert.That(record.App.Description).IsEqualTo("OpenAI app");
        await Assert.That(record.UpdateAvailable).IsTrue();
    }

    [Test]
    public async Task CheckAsync_HomebrewCask_NotFound_LeavesUnresolvedWithoutError()
    {
        var handler = new StubHttpMessageHandler().WithStatus("/api/cask/ghostcask.json", HttpStatusCode.NotFound);
        var scanner = CreateScanner(handler);
        var record = CaskRecord(scanner, "GhostCask", path: "/Applications/GhostCask.app", installed: "1.0.0");

        var results = await Check(scanner, record);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsNull();
    }

    [Test]
    public async Task CheckAsync_AlreadyResolved_PassesThroughWithoutHttpCall()
    {
        var handler = new StubHttpMessageHandler();
        var scanner = CreateScanner(handler);
        var app = new DiscoveredApp(scanner, "Resolved", new AppIdentifier("Application", "Application", "Software Update"), AppKind.App)
        {
            InstalledVersion = "1.0.0",
            LatestVersion = "2.0.0",
            Attribute = AppAttribute.None,
        };
        var record = new AppRecord(app);

        var results = await Check(scanner, record);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(handler.Requests).IsEmpty();
        await Assert.That(record.App.LatestVersion).IsEqualTo("2.0.0");
    }

    [Test]
    public async Task CheckAsync_ManuallyInstalledApp_NoUpdateMethod_IsUnresolved()
    {
        var handler = new StubHttpMessageHandler();
        var scanner = CreateScanner(handler);
        var app = new DiscoveredApp(scanner, "Manual", new AppIdentifier("Application", "Application"), AppKind.App)
        {
            InstalledVersion = "1.0.0",
            Path = "/Applications/Manual.app",
            Attribute = AppAttribute.App | AppAttribute.MacApp,
        };
        var record = new AppRecord(app);

        var results = await Check(scanner, record);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsNull();
    }

    private static MacApplicationsScanner CreateScanner(StubHttpMessageHandler handler) =>
        new(
            new PlistReader(NullLogger<PlistReader>.Instance),
            new FakeProcessRunner(),
            new StubHttpClientFactory(handler),
            NullLogger<MacApplicationsScanner>.Instance);

    private static AppRecord AppStoreRecord(
        MacApplicationsScanner scanner,
        string name,
        string bundleId,
        string installed)
    {
        var app = new DiscoveredApp(scanner, name, new AppIdentifier("Application", "Application", "App Store"), AppKind.App)
        {
            InstalledVersion = installed,
            BundleId = bundleId,
            Attribute = AppAttribute.App | AppAttribute.AppStoreApp,
        };
        return new AppRecord(app);
    }

    private static AppRecord SparkleRecord(
        MacApplicationsScanner scanner,
        string name,
        string feedUrl,
        string installed)
    {
        var app = new DiscoveredApp(scanner, name, new AppIdentifier("Application", "Application", "Sparkle"), AppKind.App)
        {
            InstalledVersion = installed,
            UpdateInfo = feedUrl,
            Attribute = AppAttribute.App | AppAttribute.SparkleFeed,
        };
        return new AppRecord(app);
    }

    private static AppRecord CaskRecord(
        MacApplicationsScanner scanner,
        string name,
        string path,
        string installed)
    {
        var app = new DiscoveredApp(scanner, name, new AppIdentifier("Application", "Application", "Cask"), AppKind.App)
        {
            InstalledVersion = installed,
            Path = path,
            Attribute = AppAttribute.App | AppAttribute.MacApp | AppAttribute.HomebrewCask,
        };
        return new AppRecord(app);
    }

    private static async Task<List<(AppRecord App, bool Error)>> Check(MacApplicationsScanner scanner, params AppRecord[] records)
    {
        var results = new List<(AppRecord App, bool Error)>();
        await foreach (var item in scanner.CheckAsync(records))
        {
            results.Add(item);
        }

        return results;
    }
}
