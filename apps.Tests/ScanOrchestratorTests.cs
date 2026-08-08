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

        await Assert.That(result.Values.Select(v => v.Name)).IsEquivalentTo(new[] { "Firefox", "Slack" });
    }

    [Test]
    public async Task ScanAsync_SkipsUnavailableScanners()
    {
        var available = new FakeScanner { Name = "A" }.Add("KeepMe");
        var unavailable = new FakeScanner { Name = "B", Available = false }.Add("DropMe");
        var orch = BuildOrchestrator(available, unavailable);

        var result = await orch.ScanAsync(kind: null);

        var names = result.Values.Select(v => v.Name).ToArray();
        await Assert.That(names).Contains("KeepMe");
        await Assert.That(names).DoesNotContain("DropMe");
    }

    [Test]
    public async Task ScanAsync_SkipsScannersThatDoNotSupportCurrentOs()
    {
        // OS.None supports neither macOS nor Windows, so it is always filtered out.
        var supported = new FakeScanner { Name = "Supported", SupportedOS = OS.MacOS | OS.Windows }.Add("Included");
        var unsupported = new FakeScanner { Name = "Unsupported", SupportedOS = OS.None }.Add("Excluded");
        var orch = BuildOrchestrator(supported, unsupported);

        var result = await orch.ScanAsync(kind: null);

        var names = result.Values.Select(v => v.Name).ToArray();
        await Assert.That(names).Contains("Included");
        await Assert.That(names).DoesNotContain("Excluded");
    }

    [Test]
    public async Task ScanAsync_WithKindFilter_SkipsScannersOfOtherKinds()
    {
        var apps = new FakeScanner { Name = "Apps", Kind = AppKind.App }.Add("GuiApp", AppKind.App);
        var devTools = new FakeScanner { Name = "Dev", Kind = AppKind.DevTool }.Add("CliTool", AppKind.DevTool);
        var orch = BuildOrchestrator(apps, devTools);

        var result = await orch.ScanAsync(kind: AppKind.App);

        var names = result.Values.Select(v => v.Name).ToArray();
        await Assert.That(names).Contains("GuiApp");
        await Assert.That(names).DoesNotContain("CliTool");
    }

    [Test]
    public async Task ScanAsync_MergesDuplicateNamesIntoSubApps()
    {
        var scanner = new FakeScanner { Name = "Dupes" }.Add("Node").Add("Node");
        var orch = BuildOrchestrator(scanner);

        var result = await orch.ScanAsync(kind: null);

        await Assert.That(result.Count).IsEqualTo(1);
        var node = result.Values.Single(v => v.Name == "Node");
        await Assert.That(node.SubApps).IsNotNull();
        await Assert.That(node.SubApps!.Count).IsEqualTo(1);
        await Assert.That(node.SubApps![0].IsDuplicate).IsTrue();
    }

    [Test]
    public async Task ScanAsync_SameNameDifferentKind_KeptSeparate_NotNested()
    {
        var scanner = new FakeScanner { Name = "Multi", Kind = AppKind.App | AppKind.Extension }
            .Add("Claude", AppKind.Extension)
            .Add("Claude", AppKind.App);
        var orch = BuildOrchestrator(scanner);

        var result = await orch.ScanAsync(kind: null);

        await Assert.That(result.Count).IsEqualTo(2);
        var values = result.Values.ToArray();
        await Assert.That(values.Any(v => v.Name == "Claude" && v.Kind == AppKind.Extension)).IsTrue();
        await Assert.That(values.Any(v => v.Name == "Claude" && v.Kind == AppKind.App)).IsTrue();
        await Assert.That(values.All(v => v.SubApps is null)).IsTrue();
    }

    [Test]
    public async Task ScanAsync_SameNameSameKindDifferentScanners_KeptSeparate()
    {
        // "EditorConfig" exists as both a JetBrains plugin and a VS Code extension — same name,
        // same kind, different source. They are unrelated software and must not be merged.
        var jetbrains = new FakeScanner { Name = "JetBrains", Kind = AppKind.Extension }.Add("EditorConfig", AppKind.Extension);
        var vscode = new FakeScanner { Name = "VS Code", Kind = AppKind.Extension }.Add("EditorConfig", AppKind.Extension);
        var orch = BuildOrchestrator(jetbrains, vscode);

        var result = await orch.ScanAsync(kind: null);

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result.Values.All(v => v.Name == "EditorConfig" && v.SubApps is null)).IsTrue();
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
