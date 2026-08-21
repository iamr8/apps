using apps.Tests.Fakes;

namespace apps.Tests;

/// <summary>Covers checklist state transitions and per-scanner progress counts.</summary>
public sealed class LiveProgressRendererTests
{
    [Test]
    public async Task Checklist_TransitionsThroughScanAndCheckStates()
    {
        var scanner = new FakeScanner { Name = "JetBrains", Kind = AppKind.Extension };
        var renderer = new LiveProgressRenderer([scanner]);

        renderer.StartScan([scanner]);
        await AssertState(renderer, scanner.Name, ChecklistProgressState.Waiting);

        renderer.RenderScannerActive(scanner.Name);
        renderer.RenderScannerProgress(scanner.Name, 2);
        await AssertState(renderer, scanner.Name, ChecklistProgressState.Scanning);

        renderer.RenderScannerDone(scanner.Name);
        await AssertState(renderer, scanner.Name, ChecklistProgressState.Waiting);

        renderer.StartCheck([(scanner, 2)]);
        renderer.RenderCheckActive(scanner.Name);
        await AssertState(renderer, scanner.Name, ChecklistProgressState.Checking);

        renderer.RenderCheckProgress(scanner.Name, updateAvailable: false, failed: false);
        var checking = renderer.GetChecklistSnapshot(scanner.Name);
        await Assert.That(checking.Checked).IsEqualTo(1);
        await Assert.That(checking.State).IsEqualTo(ChecklistProgressState.Checking);

        renderer.RenderCheckProgress(scanner.Name, updateAvailable: true, failed: false);
        renderer.RenderCheckComplete();

        var completed = renderer.GetChecklistSnapshot(scanner.Name);
        await Assert.That(completed.State).IsEqualTo(ChecklistProgressState.Completed);
        await Assert.That(completed.Discovered).IsEqualTo(2);
        await Assert.That(completed.CheckTotal).IsEqualTo(2);
        await Assert.That(completed.Checked).IsEqualTo(2);
        await Assert.That(completed.Updates).IsEqualTo(1);
        await Assert.That(completed.Failures).IsEqualTo(0);
    }

    [Test]
    public async Task Checklist_ScanFailureRemainsFailedAfterCheckStage()
    {
        var scanner = new FakeScanner { Name = "Broken" };
        var renderer = new LiveProgressRenderer([scanner]);

        renderer.StartScan([scanner]);
        renderer.RenderScannerActive(scanner.Name);
        renderer.RenderScannerFailed(scanner.Name);
        renderer.StartCheck(Array.Empty<(IScanner Scanner, int Total)>());
        renderer.RenderCheckComplete();

        await AssertState(renderer, scanner.Name, ChecklistProgressState.Failed);
    }

    [Test]
    public async Task Checklist_CheckErrorCompletesAsFailed()
    {
        var scanner = new FakeScanner { Name = "Broken" };
        var renderer = new LiveProgressRenderer([scanner]);

        renderer.StartScan([scanner]);
        renderer.RenderScannerProgress(scanner.Name, 1);
        renderer.RenderScannerDone(scanner.Name);
        renderer.StartCheck([(scanner, 1)]);
        renderer.RenderCheckActive(scanner.Name);
        renderer.RenderCheckProgress(scanner.Name, updateAvailable: false, failed: true);
        renderer.RenderCheckComplete();

        var failed = renderer.GetChecklistSnapshot(scanner.Name);
        await Assert.That(failed.State).IsEqualTo(ChecklistProgressState.Failed);
        await Assert.That(failed.Failures).IsEqualTo(1);
    }

    [Test]
    public async Task Checklist_DryRunCompletesSuccessfulScanners()
    {
        var scanner = new FakeScanner { Name = "ScanOnly" };
        var renderer = new LiveProgressRenderer([scanner]);

        renderer.StartScan([scanner]);
        renderer.RenderScannerProgress(scanner.Name, 3);
        renderer.RenderScannerDone(scanner.Name);
        renderer.RenderDryRunComplete();

        var completed = renderer.GetChecklistSnapshot(scanner.Name);
        await Assert.That(completed.State).IsEqualTo(ChecklistProgressState.Completed);
        await Assert.That(completed.Discovered).IsEqualTo(3);
    }

    private static async Task AssertState(
        LiveProgressRenderer renderer,
        string scannerName,
        ChecklistProgressState expected)
    {
        var snapshot = renderer.GetChecklistSnapshot(scannerName);
        await Assert.That(snapshot.State).IsEqualTo(expected);
    }
}
