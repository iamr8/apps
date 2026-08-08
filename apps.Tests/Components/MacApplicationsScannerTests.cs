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

    [Test]
    [Arguments(0.0, 6.0, false)]
    [Arguments(5.0, 6.0, false)]
    [Arguments(7.0, 6.0, true)]
    public async Task IsCacheStale_ComparesAgeAgainstMaxAge(double ageHours, double maxAgeHours, bool expected)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var written = now.AddHours(-ageHours);

        await Assert.That(MacApplicationsScanner.IsCacheStale(written, now, TimeSpan.FromHours(maxAgeHours))).IsEqualTo(expected);
    }

    [Test]
    public async Task IsCacheStale_NeverWritten_IsStale()
    {
        await Assert.That(MacApplicationsScanner.IsCacheStale(null, DateTimeOffset.UtcNow, TimeSpan.FromHours(6))).IsTrue();
    }

    [Test]
    public async Task GetNewestApiCacheWriteUtc_ReturnsLatestJwsWriteAcrossSubdirectories()
    {
        var dir = CreateTempDir();
        try
        {
            var older = Path.Combine(dir, "formula.jws.json");
            var internalDir = Path.Combine(dir, "internal");
            Directory.CreateDirectory(internalDir);
            var newer = Path.Combine(internalDir, "packages.arm64.jws.json");
            await File.WriteAllTextAsync(older, "{}");
            await File.WriteAllTextAsync(newer, "{}");

            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(older, t0);
            File.SetLastWriteTimeUtc(newer, t0.AddHours(3));

            var newest = MacApplicationsScanner.GetNewestApiCacheWriteUtc(dir);

            await Assert.That(newest).IsNotNull();
            await Assert.That(newest!.Value.UtcDateTime).IsEqualTo(t0.AddHours(3));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task GetNewestApiCacheWriteUtc_MissingDirectory_ReturnsNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), "apps-missing-" + Guid.NewGuid().ToString("N"));

        await Assert.That(MacApplicationsScanner.GetNewestApiCacheWriteUtc(missing)).IsNull();
    }

    [Test]
    public async Task RefreshBrewApiCacheIfStaleAsync_StaleCache_RunsBrewUpdate()
    {
        var dir = CreateTempDir();
        try
        {
            var file = Path.Combine(dir, "packages.jws.json");
            await File.WriteAllTextAsync(file, "{}");
            var now = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
            File.SetLastWriteTimeUtc(file, now.UtcDateTime.AddDays(-1));

            var runner = new FakeProcessRunner().WithSuccess("/opt/homebrew/bin/brew", "update --quiet", "Updated");
            var scanner = CreateScanner(new StubHttpMessageHandler(), runner);

            await scanner.RefreshBrewApiCacheIfStaleAsync("/opt/homebrew/bin/brew", dir, now, TimeSpan.FromHours(6), CancellationToken.None);

            await Assert.That(runner.Invocations).Contains("/opt/homebrew/bin/brew update --quiet");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task RefreshBrewApiCacheIfStaleAsync_FreshCache_SkipsBrewUpdate()
    {
        var dir = CreateTempDir();
        try
        {
            var file = Path.Combine(dir, "packages.jws.json");
            await File.WriteAllTextAsync(file, "{}");
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            File.SetLastWriteTimeUtc(file, now.UtcDateTime.AddMinutes(-5));

            var runner = new FakeProcessRunner();
            var scanner = CreateScanner(new StubHttpMessageHandler(), runner);

            await scanner.RefreshBrewApiCacheIfStaleAsync("/opt/homebrew/bin/brew", dir, now, TimeSpan.FromHours(6), CancellationToken.None);

            await Assert.That(runner.Invocations).IsEmpty();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ApplyCaskToScannedApp_SelfUpdatedSameBundle_SubAppUsesBundleVersion_UpToDate()
    {
        // OrbStack self-updated to 2.2.3; brew's receipt still says 2.2.2. The cask sub-app must
        // report the bundle's real version (2.2.3), not the stale receipt, so it is not falsely outdated.
        var scanner = CreateScanner(new StubHttpMessageHandler());
        var app = ScannedBundle(scanner, "OrbStack", path: "/Applications/OrbStack.app", installed: "2.2.3");
        var cask = OrbCask(installed: "2.2.2", latest: "2.2.3", target: "/Applications/OrbStack.app");

        scanner.ApplyCaskToScannedApp(app, cask);

        await Assert.That(app.SubApps!.Count).IsEqualTo(1);
        var sub = app.SubApps[0];
        await Assert.That(sub.InstalledVersion).IsEqualTo("2.2.3");
        await Assert.That(sub.LatestVersion).IsEqualTo("2.2.3");
        await Assert.That(sub.Attribute.HasFlag(AppAttribute.HomebrewCask)).IsTrue();
        await Assert.That(new AppRecord(sub).UpdateAvailable).IsFalse();
        await Assert.That(new AppRecord(app).HasUpdate).IsFalse();
    }

    [Test]
    public async Task ApplyCaskToScannedApp_SameBundleGenuinelyOutdated_SubAppOutdated_ParentRollsUp()
    {
        var scanner = CreateScanner(new StubHttpMessageHandler());
        var app = ScannedBundle(scanner, "OrbStack", path: "/Applications/OrbStack.app", installed: "2.2.2");
        var cask = OrbCask(installed: "2.2.2", latest: "2.2.3", target: "/Applications/OrbStack.app");

        scanner.ApplyCaskToScannedApp(app, cask);

        var sub = app.SubApps![0];
        await Assert.That(sub.InstalledVersion).IsEqualTo("2.2.2");
        await Assert.That(sub.LatestVersion).IsEqualTo("2.2.3");
        await Assert.That(new AppRecord(sub).UpdateAvailable).IsTrue();
        // Parent's own version is current, but it rolls up the sub-app so the default view still shows it.
        await Assert.That(new AppRecord(app).UpdateAvailable).IsFalse();
        await Assert.That(new AppRecord(app).HasUpdate).IsTrue();
    }

    [Test]
    public async Task ApplyCaskToScannedApp_DifferentBundleSameName_SubAppKeepsCaskVersion()
    {
        // Cask manages a different bundle than the scanned app → not the same install, so the
        // sub-app keeps the cask's own recorded version rather than the parent's.
        var scanner = CreateScanner(new StubHttpMessageHandler());
        var app = ScannedBundle(scanner, "Foo", path: "/Applications/Foo.app", installed: "1.0");
        var cask = OrbCask(installed: "3.0", latest: "3.1", target: "/Applications/Bar.app");

        scanner.ApplyCaskToScannedApp(app, cask);

        var sub = app.SubApps![0];
        await Assert.That(sub.InstalledVersion).IsEqualTo("3.0");
        await Assert.That(sub.LatestVersion).IsEqualTo("3.1");
        await Assert.That(app.LatestVersion).IsNull();
        await Assert.That(app.Attribute.HasFlag(AppAttribute.HomebrewCask)).IsFalse();
        await Assert.That(new AppRecord(app).HasUpdate).IsTrue();
    }

    [Test]
    public async Task ApplyCaskToScannedApp_CaskWithoutExplicitTarget_MatchedByAppName_UsesBundleVersion()
    {
        var scanner = CreateScanner(new StubHttpMessageHandler());
        var app = ScannedBundle(scanner, "OrbStack", path: "/Applications/OrbStack.app", installed: "2.2.3");
        var cask = new BrewCaskRecord
        {
            Token = "orbstack",
            Name = ["OrbStack"],
            Description = "d",
            LatestVersion = "2.2.3",
            InstalledVersion = "2.2.2",
            Artifacts = [new BrewCaskArtifact { App = ["OrbStack.app"] }], // no Target, bare .app name
        };

        scanner.ApplyCaskToScannedApp(app, cask);

        await Assert.That(app.SubApps![0].InstalledVersion).IsEqualTo("2.2.3");
        await Assert.That(new AppRecord(app.SubApps[0]).UpdateAvailable).IsFalse();
    }

    [Test]
    public async Task ApplyCaskToScannedApp_MatchedByBundleId_UsesBundleVersion()
    {
        var scanner = CreateScanner(new StubHttpMessageHandler());
        var app = ScannedBundle(scanner, "Widget", path: "/Applications/Widget.app", installed: "5.0");
        app.BundleId = "com.acme.widget";
        var cask = new BrewCaskRecord
        {
            Token = "widget",
            Name = ["Widget"],
            Description = "d",
            LatestVersion = "5.0",
            InstalledVersion = "4.0",
            Artifacts = [new BrewCaskArtifact { App = ["com.acme.widget"] }],
        };

        scanner.ApplyCaskToScannedApp(app, cask);

        await Assert.That(app.SubApps![0].InstalledVersion).IsEqualTo("5.0");
        await Assert.That(new AppRecord(app.SubApps[0]).UpdateAvailable).IsFalse();
    }

    [Test]
    public async Task ApplyCaskToScannedApp_PathDiffersOnlyByCase_UsesBundleVersion()
    {
        var scanner = CreateScanner(new StubHttpMessageHandler());
        var app = ScannedBundle(scanner, "OrbStack", path: "/Applications/OrbStack.app", installed: "2.2.3");
        var cask = OrbCask(installed: "2.2.2", latest: "2.2.3", target: "/applications/orbstack.app/");

        scanner.ApplyCaskToScannedApp(app, cask);

        await Assert.That(app.SubApps![0].InstalledVersion).IsEqualTo("2.2.3");
    }

    [Test]
    public async Task ApplyCaskToScannedApp_NoArtifacts_TrustsNameMatch_UsesBundleVersion()
    {
        var scanner = CreateScanner(new StubHttpMessageHandler());
        var app = ScannedBundle(scanner, "OrbStack", path: "/Applications/OrbStack.app", installed: "2.2.3");
        var cask = new BrewCaskRecord
        {
            Token = "orbstack",
            Name = ["OrbStack"],
            Description = "d",
            LatestVersion = "2.2.3",
            InstalledVersion = "2.2.2",
            Artifacts = null,
        };

        scanner.ApplyCaskToScannedApp(app, cask);

        await Assert.That(app.SubApps![0].InstalledVersion).IsEqualTo("2.2.3");
        await Assert.That(new AppRecord(app.SubApps[0]).UpdateAvailable).IsFalse();
    }

    [Test]
    public async Task CaskArtifactMatchesApp_NameDiffers_ButPathMatches_ReturnsTrue()
    {
        // Manual bundle scanned under one display name; cask names it differently but its
        // artifact target/app resolves to the same bundle → cross-source dedup should collapse them.
        var scanner = CreateScanner(new StubHttpMessageHandler());
        var manual = ScannedBundle(scanner, "Visual Studio Code", path: "/Applications/Visual Studio Code.app", installed: "1.90");
        var cask = new BrewCaskRecord
        {
            Token = "visual-studio-code",
            Name = ["Microsoft Visual Studio Code"],
            Description = "d",
            LatestVersion = "1.95",
            InstalledVersion = "1.90",
            Artifacts = [new BrewCaskArtifact { App = ["Visual Studio Code.app"], Target = "/Applications/Visual Studio Code.app" }],
        };

        await Assert.That(MacApplicationsScanner.CaskArtifactMatchesApp(cask, manual)).IsTrue();
        // name-fallback must NOT fire across unrelated apps
        await Assert.That(MacApplicationsScanner.CaskArtifactMatchesApp(cask, ScannedBundle(scanner, "Other", "/Applications/Other.app", "1.0"))).IsFalse();
    }

    [Test]
    public async Task CaskInstallsApp_DifferentBundle_NoSignalMatches_ReturnsFalse()
    {
        var scanner = CreateScanner(new StubHttpMessageHandler());
        var app = ScannedBundle(scanner, "OrbStack", path: "/Applications/OrbStack.app", installed: "2.2.3");
        app.BundleId = "com.orbstack.OrbStack";
        var cask = new BrewCaskRecord
        {
            Token = "orbstack",
            Name = ["OrbStack"],
            Description = "d",
            LatestVersion = "9.1",
            InstalledVersion = "9.0",
            Artifacts = [new BrewCaskArtifact { App = ["OrbStackHelper.app"], Target = "/Applications/OrbStackHelper.app" }],
        };

        await Assert.That(MacApplicationsScanner.CaskInstallsApp(cask, app)).IsFalse();
    }

    private static DiscoveredApp ScannedBundle(
        MacApplicationsScanner scanner,
        string name,
        string path,
        string installed,
        bool appStore = false)
    {
        var attribute = AppAttribute.App | AppAttribute.MacApp | (appStore ? AppAttribute.AppStoreApp : AppAttribute.None);
        return new DiscoveredApp(scanner, name, new AppIdentifier("Application", "Application"), AppKind.App)
        {
            InstalledVersion = installed,
            Path = path,
            Attribute = attribute,
        };
    }

    private static BrewCaskRecord OrbCask(string installed, string latest, string target) =>
        new()
        {
            Token = "orbstack",
            Name = ["OrbStack"],
            Description = "Fast Docker and Linux",
            LatestVersion = latest,
            InstalledVersion = installed,
            Artifacts = [new BrewCaskArtifact { App = ["OrbStack.app"], Target = target }],
        };

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "apps-brewcache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static MacApplicationsScanner CreateScanner(StubHttpMessageHandler handler, IProcessRunner? runner = null) =>
        new(
            new PlistReader(NullLogger<PlistReader>.Instance),
            runner ?? new FakeProcessRunner(),
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
