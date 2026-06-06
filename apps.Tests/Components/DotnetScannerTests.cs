using System.Net;

using apps.Components.Dotnet;
using apps.Tests.Fakes;

using Microsoft.Extensions.Logging.Abstractions;

namespace apps.Tests.Components;

/// <summary>
/// Covers <see cref="DotnetScanner"/>: the <c>--list-sdks</c> / <c>--list-runtimes</c> /
/// <c>tool list -g</c> parsers, NuGet stable-version selection, and the <c>CheckAsync</c>
/// update-check flow against a stubbed .NET releases index and NuGet registry.
/// </summary>
public sealed class DotnetScannerTests
{
    [Test]
    public async Task ParseSdks_ReadsLatestPerChannel()
    {
        const string output = """
                              6.0.428 [/usr/local/share/dotnet/sdk]
                              8.0.100 [/usr/local/share/dotnet/sdk]
                              8.0.405 [/usr/local/share/dotnet/sdk]
                              """;
        var sdks = DotnetScanner.ParseSdks(output);

        await Assert.That(sdks.Length).IsEqualTo(2);
        await Assert.That(sdks.Any(s => s is { Name: "6.0", Version: "6.0.428" })).IsTrue();
        await Assert.That(sdks.Any(s => s is { Name: "8.0", Version: "8.0.405" })).IsTrue();
    }

    [Test]
    public async Task ParseSdks_KeepsInstallPath()
    {
        var sdks = DotnetScanner.ParseSdks("8.0.405 [/usr/local/share/dotnet/sdk]");

        await Assert.That(sdks.Length).IsEqualTo(1);
        await Assert.That(sdks[0].Path).IsEqualTo("/usr/local/share/dotnet/sdk");
    }

    [Test]
    public async Task ParseSdks_EmptyOutput_ReturnsEmpty()
    {
        await Assert.That(DotnetScanner.ParseSdks("")).IsEmpty();
        await Assert.That(DotnetScanner.ParseSdks("   \n  ")).IsEmpty();
    }

    [Test]
    public async Task ParseRuntimes_GroupsByNameAndChannelKeepingLatest()
    {
        const string output = """
                              Microsoft.AspNetCore.App 8.0.1 [/usr/local/share/dotnet/shared/Microsoft.AspNetCore.App]
                              Microsoft.AspNetCore.App 8.0.4 [/usr/local/share/dotnet/shared/Microsoft.AspNetCore.App]
                              Microsoft.NETCore.App 8.0.4 [/usr/local/share/dotnet/shared/Microsoft.NETCore.App]
                              """;
        var runtimes = DotnetScanner.ParseRuntimes(output);

        await Assert.That(runtimes.Length).IsEqualTo(2);
        await Assert.That(runtimes.Any(r => r is { Name: "Microsoft.AspNetCore.App 8.0", Version: "8.0.4" })).IsTrue();
        await Assert.That(runtimes.Any(r => r is { Name: "Microsoft.NETCore.App 8.0", Version: "8.0.4" })).IsTrue();
    }

    [Test]
    public async Task ParseRuntimes_SeparatesDistinctChannels()
    {
        const string output = """
                              Microsoft.NETCore.App 6.0.36 [/share/Microsoft.NETCore.App]
                              Microsoft.NETCore.App 8.0.4 [/share/Microsoft.NETCore.App]
                              """;
        var runtimes = DotnetScanner.ParseRuntimes(output);

        await Assert.That(runtimes.Length).IsEqualTo(2);
    }

    [Test]
    public async Task ParseGlobalTools_SkipsTwoHeaderLinesAndReadsEntries()
    {
        const string output = """
                              Package Id      Version      Commands
                              -------------------------------------------
                              dotnet-ef       8.0.4        dotnet-ef
                              csharprepl      0.6.7        csharprepl
                              """;
        var tools = DotnetScanner.ParseGlobalTools(output);

        await Assert.That(tools.Length).IsEqualTo(2);
        await Assert.That(tools.Any(t => t is { Name: "dotnet-ef", Version: "8.0.4" })).IsTrue();
        await Assert.That(tools.Any(t => t is { Name: "csharprepl", Version: "0.6.7" })).IsTrue();
    }

    [Test]
    public async Task ParseGlobalTools_HeaderOnly_ReturnsEmpty()
    {
        const string output = """
                              Package Id      Version      Commands
                              -------------------------------------------
                              """;
        await Assert.That(DotnetScanner.ParseGlobalTools(output)).IsEmpty();
    }

    [Test]
    [Arguments(new[] { "1.0.0", "1.1.0", "2.0.0" }, "2.0.0")]
    [Arguments(new[] { "1.0.0", "2.0.0-preview.1", "2.0.0-rc.2" }, "1.0.0")]
    [Arguments(new[] { "1.0.0-alpha", "2.0.0-beta" }, "2.0.0-beta")]
    public async Task SelectLatestStableVersion_PrefersLatestStable(string[] versions, string expected)
    {
        await Assert.That(DotnetScanner.SelectLatestStableVersion(versions)).IsEqualTo(expected);
    }

    [Test]
    public async Task SelectLatestStableVersion_NullOrEmpty_ReturnsNull()
    {
        await Assert.That(DotnetScanner.SelectLatestStableVersion(null)).IsNull();
        await Assert.That(DotnetScanner.SelectLatestStableVersion([])).IsNull();
    }

    [Test]
    public async Task CheckAsync_GlobalTool_WhenNuGetHasNewerVersion_SetsLatestAndUpdateAvailable()
    {
        var handler = new StubHttpMessageHandler()
            .WithJson(ReleasesIndexPath, EmptyReleasesIndex)
            .WithJson("/v3-flatcontainer/dotnet-ef/index.json", """{ "versions": ["8.0.1", "8.0.4"] }""");
        var scanner = CreateScanner(handler);
        var record = ToolRecord("dotnet-ef", installed: "8.0.1");

        var results = await Check(scanner, record);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsEqualTo("8.0.4");
        await Assert.That(record.UpdateAvailable).IsTrue();
    }

    [Test]
    public async Task CheckAsync_GlobalTool_WhenUpToDate_NoUpdateAvailable()
    {
        var handler = new StubHttpMessageHandler()
            .WithJson(ReleasesIndexPath, EmptyReleasesIndex)
            .WithJson("/v3-flatcontainer/dotnet-ef/index.json", """{ "versions": ["8.0.1", "8.0.4"] }""");
        var scanner = CreateScanner(handler);
        var record = ToolRecord("dotnet-ef", installed: "8.0.4");

        await Check(scanner, record);

        await Assert.That(record.App.LatestVersion).IsEqualTo("8.0.4");
        await Assert.That(record.UpdateAvailable).IsFalse();
    }

    [Test]
    public async Task CheckAsync_GlobalTool_WhenNuGetReturns404_LeavesLatestUnsetWithoutError()
    {
        var handler = new StubHttpMessageHandler()
            .WithJson(ReleasesIndexPath, EmptyReleasesIndex)
            .WithStatus("/v3-flatcontainer/ghost/index.json", HttpStatusCode.NotFound);
        var scanner = CreateScanner(handler);
        var record = ToolRecord("ghost", installed: "1.0.0");

        var results = await Check(scanner, record);

        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsNull();
        await Assert.That(record.UpdateAvailable).IsFalse();
    }

    [Test]
    public async Task CheckAsync_GlobalTool_LowercasesPackageIdInRequestPath()
    {
        var handler = new StubHttpMessageHandler()
            .WithJson(ReleasesIndexPath, EmptyReleasesIndex)
            .WithJson("/v3-flatcontainer/dotnet-ef/index.json", """{ "versions": ["9.0.0"] }""");
        var scanner = CreateScanner(handler);
        var record = ToolRecord("Dotnet-EF", installed: "8.0.0");

        await Check(scanner, record);

        await Assert.That(record.App.LatestVersion).IsEqualTo("9.0.0");
        await Assert.That(handler.Requests.Any(u => u.AbsolutePath == "/v3-flatcontainer/dotnet-ef/index.json")).IsTrue();
    }

    [Test]
    public async Task CheckAsync_Sdk_WhenChannelHasNewerVersion_SetsLatestSdk()
    {
        var handler = new StubHttpMessageHandler().WithJson(ReleasesIndexPath, """
            {
              "releases-index": [
                { "channel-version": "8.0", "latest-release": "8.0.4", "latest-sdk": "8.0.405", "latest-runtime": "8.0.4", "support-phase": "active", "releases.json": "x" }
              ]
            }
            """);
        var scanner = CreateScanner(handler);
        var record = SdkRecord("8.0.100");

        var results = await Check(scanner, record);

        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsEqualTo("8.0.405");
        await Assert.That(record.UpdateAvailable).IsTrue();
    }

    [Test]
    public async Task CheckAsync_Runtime_UsesLatestRuntimeFromChannel()
    {
        var handler = new StubHttpMessageHandler().WithJson(ReleasesIndexPath, """
            {
              "releases-index": [
                { "channel-version": "8.0", "latest-release": "8.0.4", "latest-sdk": "8.0.405", "latest-runtime": "8.0.4", "support-phase": "active", "releases.json": "x" }
              ]
            }
            """);
        var scanner = CreateScanner(handler);
        var record = RuntimeRecord("8.0.1");

        await Check(scanner, record);

        await Assert.That(record.App.LatestVersion).IsEqualTo("8.0.4");
        await Assert.That(record.UpdateAvailable).IsTrue();
    }

    [Test]
    public async Task CheckAsync_Sdk_WhenChannelMissing_NoErrorAndLatestUnset()
    {
        var handler = new StubHttpMessageHandler().WithJson(ReleasesIndexPath, EmptyReleasesIndex);
        var scanner = CreateScanner(handler);
        var record = SdkRecord("8.0.100");

        var results = await Check(scanner, record);

        await Assert.That(results[0].Error).IsFalse();
        await Assert.That(record.App.LatestVersion).IsNull();
    }

    [Test]
    public async Task CheckAsync_Sdk_WhenReleasesIndexFetchFails_ReportsError()
    {
        var handler = new StubHttpMessageHandler().WithStatus(ReleasesIndexPath, HttpStatusCode.InternalServerError);
        var scanner = CreateScanner(handler);
        var record = SdkRecord("8.0.100");

        var results = await Check(scanner, record);

        await Assert.That(results[0].Error).IsTrue();
        await Assert.That(record.App.LatestVersion).IsNull();
    }

    private const string ReleasesIndexPath = "/dotnet/release-metadata/releases-index.json";
    private const string EmptyReleasesIndex = """{ "releases-index": [] }""";

    private static DotnetScanner CreateScanner(StubHttpMessageHandler handler) =>
        new(new StubHttpClientFactory(handler), new FakeProcessRunner(), NullLogger<DotnetScanner>.Instance);

    private static AppRecord ToolRecord(string name, string installed)
    {
        var app = new DiscoveredApp(
            new FakeScanner { Name = "Dotnet" },
            name,
            new AppIdentifier("Dotnet", ".NET", "Global Tool"),
            AppKind.DevTool)
        {
            InstalledVersion = installed,
            Attribute = AppAttribute.DevTool,
        };
        return new AppRecord(app);
    }

    private static AppRecord SdkRecord(string installed)
    {
        var app = new DiscoveredApp(
            new FakeScanner { Name = "Dotnet" },
            $".NET {installed}",
            new AppIdentifier("Dotnet", ".NET", "Sdk"),
            AppKind.DevTool)
        {
            InstalledVersion = installed,
            Attribute = AppAttribute.DevTool | AppAttribute.Sdk,
        };
        return new AppRecord(app);
    }

    private static AppRecord RuntimeRecord(string installed)
    {
        var app = new DiscoveredApp(
            new FakeScanner { Name = "Dotnet" },
            "Microsoft.NETCore.App",
            new AppIdentifier("Dotnet", ".NET", "Runtime"),
            AppKind.DevTool)
        {
            InstalledVersion = installed,
            Attribute = AppAttribute.DevTool | AppAttribute.Sdk,
        };
        return new AppRecord(app);
    }

    private static async Task<List<(AppRecord App, bool Error)>> Check(DotnetScanner scanner, params AppRecord[] records)
    {
        var results = new List<(AppRecord App, bool Error)>();
        await foreach (var item in scanner.CheckAsync(records))
        {
            results.Add(item);
        }

        return results;
    }
}
