using System.Net;

using apps.Components.VsCode;
using apps.Tests.Fakes;

using Microsoft.Extensions.Logging.Abstractions;

namespace apps.Tests.Components;

/// <summary>
/// Covers <see cref="VsCodeExtScanner"/>: the <c>--list-extensions</c> line parser, the
/// Marketplace gallery query-body builder, the stable-version picker, and the
/// <c>CheckAsync</c> flow against a stubbed gallery API.
/// </summary>
public sealed class VsCodeExtScannerTests
{
    [Test]
    [Arguments("ms-python.python@2024.4.1", "ms-python.python", "2024.4.1")]
    [Arguments("esbenp.prettier-vscode@10.4.0", "esbenp.prettier-vscode", "10.4.0")]
    [Arguments("publisher.name@1.2.3-beta.1", "publisher.name", "1.2.3-beta.1")]
    public async Task ParseExtensionLine_SplitsIdAndVersion(string line, string expectedId, string expectedVersion)
    {
        var parsed = VsCodeExtScanner.ParseExtensionLine(line);

        await Assert.That(parsed).IsNotNull();
        await Assert.That(parsed!.Value.ExtensionId).IsEqualTo(expectedId);
        await Assert.That(parsed.Value.Version).IsEqualTo(expectedVersion);
    }

    [Test]
    public async Task ParseExtensionLine_UsesLastAtSeparator()
    {
        var parsed = VsCodeExtScanner.ParseExtensionLine("scope@name.ext@9.9.9");

        await Assert.That(parsed).IsNotNull();
        await Assert.That(parsed!.Value.ExtensionId).IsEqualTo("scope@name.ext");
        await Assert.That(parsed.Value.Version).IsEqualTo("9.9.9");
    }

    [Test]
    public async Task ParseExtensionLine_NoAtSign_ReturnsNull()
    {
        await Assert.That(VsCodeExtScanner.ParseExtensionLine("ms-python.python")).IsNull();
    }

    [Test]
    public async Task ParseExtensionLine_BlankExtensionId_ReturnsNull()
    {
        await Assert.That(VsCodeExtScanner.ParseExtensionLine("@1.2.3")).IsNull();
    }

    [Test]
    public async Task ParseExtensionLine_TrailingAt_YieldsEmptyVersion()
    {
        var parsed = VsCodeExtScanner.ParseExtensionLine("ms-python.python@");

        await Assert.That(parsed).IsNotNull();
        await Assert.That(parsed!.Value.ExtensionId).IsEqualTo("ms-python.python");
        await Assert.That(parsed.Value.Version).IsEqualTo(string.Empty);
    }

    [Test]
    [Arguments("%displayName%", true)]
    [Arguments("%extension.displayName%", true)]
    [Arguments("%%", true)]
    [Arguments("GitHub Pull Requests", false)]
    [Arguments("100% Cotton", false)]
    [Arguments("%partial", false)]
    [Arguments("", false)]
    public async Task IsNlsPlaceholder_DetectsUnresolvedLocalizationKeys(string value, bool expected)
    {
        await Assert.That(VsCodeExtScanner.IsNlsPlaceholder(value)).IsEqualTo(expected);
    }

    [Test]
    public async Task GetLatestStableVersion_PrefersFirstStableOverPreRelease()
    {
        VsCodeExtVersion[] versions =
        [
            new() { Version = "3.0.0-rc", Properties = [PreRelease(true)] },
            new() { Version = "2.5.0", Properties = [PreRelease(false)] },
            new() { Version = "2.0.0" }
        ];

        await Assert.That(VsCodeExtScanner.GetLatestStableVersion(versions)).IsEqualTo("2.5.0");
    }

    [Test]
    public async Task GetLatestStableVersion_NoPreReleaseProperty_TreatedAsStable()
    {
        VsCodeExtVersion[] versions = [new() { Version = "1.4.2" }];

        await Assert.That(VsCodeExtScanner.GetLatestStableVersion(versions)).IsEqualTo("1.4.2");
    }

    [Test]
    public async Task GetLatestStableVersion_AllPreRelease_FallsBackToFirst()
    {
        VsCodeExtVersion[] versions =
        [
            new() { Version = "2.0.0-beta", Properties = [PreRelease(true)] },
            new() { Version = "1.0.0-beta", Properties = [PreRelease(true)] }
        ];

        await Assert.That(VsCodeExtScanner.GetLatestStableVersion(versions)).IsEqualTo("2.0.0-beta");
    }

    [Test]
    public async Task GetLatestStableVersion_Null_ReturnsNull()
    {
        await Assert.That(VsCodeExtScanner.GetLatestStableVersion(null)).IsNull();
    }

    [Test]
    public async Task GetLatestStableVersion_Empty_ReturnsNull()
    {
        await Assert.That(VsCodeExtScanner.GetLatestStableVersion([])).IsNull();
    }

    [Test]
    public async Task BuildQueryRequest_AddsOneExtensionNameCriterionPerRecord()
    {
        var scanner = CreateScanner(new StubHttpMessageHandler());
        var records = new[]
        {
            ExtensionRecord(scanner, "ms-python.python", installed: "2024.1.0"),
            ExtensionRecord(scanner, "esbenp.prettier-vscode", installed: "10.0.0")
        };

        var request = VsCodeExtScanner.BuildQueryRequest(records);
        var filter = request.Filters!.Single();

        await Assert.That(request.Flags).IsEqualTo(1 | 16);
        await Assert.That(filter.PageSize).IsEqualTo(2);
        await Assert.That(filter.PageNumber).IsEqualTo(1);
        await Assert.That(filter.Criteria!.Length).IsEqualTo(2);
        await Assert.That(filter.Criteria!.All(c => c.FilterType == 7)).IsTrue();
        await Assert.That(filter.Criteria!.Select(c => c.Value)).Contains("ms-python.python");
        await Assert.That(filter.Criteria!.Select(c => c.Value)).Contains("esbenp.prettier-vscode");
    }

    [Test]
    public async Task BuildQueryRequest_FallsBackToNameWhenPackageIdMissing()
    {
        var scanner = CreateScanner(new StubHttpMessageHandler());
        var app = new DiscoveredApp(scanner, "fallback.name", new AppIdentifier("VSCode", "VS Code", "Extension"), AppKind.Extension)
        {
            InstalledVersion = "1.0.0",
            Attribute = AppAttribute.VsCodeExtension
        };

        var request = VsCodeExtScanner.BuildQueryRequest([new AppRecord(app)]);

        await Assert.That(request.Filters!.Single().Criteria!.Single().Value).IsEqualTo("fallback.name");
    }

    [Test]
    public async Task CheckAsync_EmptyInput_MakesNoRequest()
    {
        var handler = new StubHttpMessageHandler();
        var scanner = CreateScanner(handler);

        var results = await Check(scanner);

        await Assert.That(results).IsEmpty();
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task CheckAsync_WhenGalleryHasNewerVersion_SetsLatestAndUpdateAvailable()
    {
        const string response = """
                                {
                                  "results": [
                                    { "extensions": [
                                        {
                                          "extensionName": "python",
                                          "publisher": { "publisherName": "ms-python" },
                                          "versions": [ { "version": "2024.6.0" } ]
                                        }
                                    ] }
                                  ]
                                }
                                """;
        var handler = new StubHttpMessageHandler().WithJson(GalleryPath, response);
        var scanner = CreateScanner(handler);
        var record = ExtensionRecord(scanner, "ms-python.python", installed: "2024.1.0");

        var results = await Check(scanner, record);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(results[0].App).IsSameReferenceAs(record);
        await Assert.That(record.App.LatestVersion).IsEqualTo("2024.6.0");
        await Assert.That(record.UpdateAvailable).IsTrue();
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
        await Assert.That(handler.Requests[0].AbsolutePath).IsEqualTo(GalleryPath);
    }

    [Test]
    public async Task CheckAsync_WhenUpToDate_SetsLatestWithoutUpdateAvailable()
    {
        const string response = """
                                {
                                  "results": [
                                    { "extensions": [
                                        {
                                          "extensionName": "python",
                                          "publisher": { "publisherName": "ms-python" },
                                          "versions": [ { "version": "2024.6.0" } ]
                                        }
                                    ] }
                                  ]
                                }
                                """;
        var handler = new StubHttpMessageHandler().WithJson(GalleryPath, response);
        var scanner = CreateScanner(handler);
        var record = ExtensionRecord(scanner, "ms-python.python", installed: "2024.6.0");

        await Check(scanner, record);

        await Assert.That(record.App.LatestVersion).IsEqualTo("2024.6.0");
        await Assert.That(record.UpdateAvailable).IsFalse();
    }

    [Test]
    public async Task CheckAsync_WhenGalleryReturns500_MarksEveryRecordAsError()
    {
        var handler = new StubHttpMessageHandler().WithStatus(GalleryPath, HttpStatusCode.InternalServerError);
        var scanner = CreateScanner(handler);
        var first = ExtensionRecord(scanner, "ms-python.python", installed: "2024.1.0");
        var second = ExtensionRecord(scanner, "esbenp.prettier-vscode", installed: "10.0.0");

        var results = await Check(scanner, first, second);

        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(results.All(r => r.Error)).IsTrue();
    }

    [Test]
    public async Task CheckAsync_ResponseWithNoExtensions_EmitsNothing()
    {
        const string response = """{ "results": [ { "extensions": [] } ] }""";
        var handler = new StubHttpMessageHandler().WithJson(GalleryPath, response);
        var scanner = CreateScanner(handler);
        var record = ExtensionRecord(scanner, "ms-python.python", installed: "2024.1.0");

        var results = await Check(scanner, record);

        await Assert.That(results).IsEmpty();
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CheckAsync_ResponseExtensionMatchesNoRecord_MarksBatchAsError()
    {
        const string response = """
                                {
                                  "results": [
                                    { "extensions": [
                                        {
                                          "extensionName": "python",
                                          "publisher": { "publisherName": "ms-python" },
                                          "versions": [ { "version": "2024.6.0" } ]
                                        }
                                    ] }
                                  ]
                                }
                                """;
        var handler = new StubHttpMessageHandler().WithJson(GalleryPath, response);
        var scanner = CreateScanner(handler);
        var unrelated = ExtensionRecord(scanner, "esbenp.prettier-vscode", installed: "10.0.0");

        var results = await Check(scanner, unrelated);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsTrue();
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    private static VsCodeExtScanner CreateScanner(StubHttpMessageHandler handler) =>
        new(new FakeProcessRunner(), new StubHttpClientFactory(handler), NullLogger<VsCodeExtScanner>.Instance);

    private static AppRecord ExtensionRecord(VsCodeExtScanner scanner, string extensionId, string installed)
    {
        var app = new DiscoveredApp(scanner,
            extensionId,
            new AppIdentifier("VSCode", "VS Code", "Extension"),
            AppKind.Extension)
        {
            PackageId = extensionId,
            InstalledVersion = installed,
            UpdateInfo = extensionId,
            Attribute = AppAttribute.VsCodeExtension
        };
        return new AppRecord(app);
    }

    private static VsCodeExtProperty PreRelease(bool isPreRelease) =>
        new() { Key = "Microsoft.VisualStudio.Code.PreRelease", Value = isPreRelease ? "true" : "false" };

    private static async Task<List<(AppRecord App, bool Error)>> Check(VsCodeExtScanner scanner, params AppRecord[] records)
    {
        var results = new List<(AppRecord App, bool Error)>();
        await foreach (var item in scanner.CheckAsync(records))
        {
            results.Add(item);
        }

        return results;
    }

    private const string GalleryPath = "/_apis/public/gallery/extensionquery";
}
