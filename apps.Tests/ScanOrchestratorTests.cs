using apps.Tests.Fakes;

using Microsoft.Extensions.Logging.Abstractions;

namespace apps.Tests;

/// <summary>
/// Covers <see cref="ScanOrchestrator"/> scanner selection (availability, OS, kind filtering)
/// and the duplicate-name merge into <c>SubApps</c>. Uses fake scanners and a stub HTTP factory
/// for connection warm-up.
/// </summary>
public sealed class ScanOrchestratorTests
{
    [Test]
    public async Task ScanAsync_ReturnsAppsFromAvailableScanners()
    {
        var brew = new FakeScanner { Name = "Brew" }.Add("Firefox").Add("Slack");
        var orch = BuildOrchestrator(brew);

        var result = await orch.ScanAsync(kind: null);

        await Assert.That(result.Keys).IsEquivalentTo(new[] { "Firefox", "Slack" });
    }

    [Test]
    public async Task ScanAsync_SkipsUnavailableScanners()
    {
        var available = new FakeScanner { Name = "A" }.Add("KeepMe");
        var unavailable = new FakeScanner { Name = "B", Available = false }.Add("DropMe");
        var orch = BuildOrchestrator(available, unavailable);

        var result = await orch.ScanAsync(kind: null);

        await Assert.That(result.ContainsKey("KeepMe")).IsTrue();
        await Assert.That(result.ContainsKey("DropMe")).IsFalse();
    }

    [Test]
    public async Task ScanAsync_SkipsScannersThatDoNotSupportCurrentOs()
    {
        // OS.None supports neither macOS nor Windows, so it is always filtered out.
        var supported = new FakeScanner { Name = "Supported", SupportedOS = OS.MacOS | OS.Windows }.Add("Included");
        var unsupported = new FakeScanner { Name = "Unsupported", SupportedOS = OS.None }.Add("Excluded");
        var orch = BuildOrchestrator(supported, unsupported);

        var result = await orch.ScanAsync(kind: null);

        await Assert.That(result.ContainsKey("Included")).IsTrue();
        await Assert.That(result.ContainsKey("Excluded")).IsFalse();
    }

    [Test]
    public async Task ScanAsync_WithKindFilter_SkipsScannersOfOtherKinds()
    {
        var apps = new FakeScanner { Name = "Apps", Kind = AppKind.App }.Add("GuiApp", AppKind.App);
        var devTools = new FakeScanner { Name = "Dev", Kind = AppKind.DevTool }.Add("CliTool", AppKind.DevTool);
        var orch = BuildOrchestrator(apps, devTools);

        var result = await orch.ScanAsync(kind: AppKind.App);

        await Assert.That(result.ContainsKey("GuiApp")).IsTrue();
        await Assert.That(result.ContainsKey("CliTool")).IsFalse();
    }

    [Test]
    public async Task ScanAsync_MergesDuplicateNamesIntoSubApps()
    {
        var scanner = new FakeScanner { Name = "Dupes" }.Add("Node").Add("Node");
        var orch = BuildOrchestrator(scanner);

        var result = await orch.ScanAsync(kind: null);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result.ContainsKey("Node")).IsTrue();
        await Assert.That(result["Node"].SubApps).IsNotNull();
        await Assert.That(result["Node"].SubApps!.Count).IsEqualTo(1);
        await Assert.That(result["Node"].SubApps![0].IsDuplicate).IsTrue();
    }

    [Test]
    public async Task ScanAsync_NoScanners_ReturnsEmpty()
    {
        var orch = BuildOrchestrator();
        var result = await orch.ScanAsync(kind: null);
        await Assert.That(result).IsEmpty();
    }

    private static ScanOrchestrator BuildOrchestrator(params IScanner[] scanners)
    {
        var warmup = new ConnectionWarmup(new StubHttpClientFactory(new StubHttpMessageHandler()));
        var renderer = new LiveProgressRenderer(scanners);
        return new ScanOrchestrator(scanners, warmup, renderer, NullLogger<ScanOrchestrator>.Instance);
    }
}
