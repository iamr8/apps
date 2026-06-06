using apps.Tests.Fakes;

using Microsoft.Extensions.Logging.Abstractions;

namespace apps.Tests;

/// <summary>
/// Covers <see cref="CheckOrchestrator"/>: grouping records by their owning scanner and
/// tallying the (total, updates, errors) summary as checks complete.
/// </summary>
public sealed class CheckOrchestratorTests
{
    [Test]
    public async Task CheckAsync_TalliesTotalUpdatesAndErrors()
    {
        var scanner = new FakeScanner
        {
            Name = "S",
            Kind = AppKind.App,
            OnCheck = record =>
            {
                if (record.App.Name == "bad")
                {
                    return (record, true);
                }

                record.App.LatestVersion = "2.0.0";
                return (record, false);
            },
        };

        var records = new[]
        {
            Record(scanner, "outdated", "1.0.0"), // becomes update (latest 2.0.0)
            Record(scanner, "current", "2.0.0"), // latest set to 2.0.0 → no update
            Record(scanner, "bad", "1.0.0"), // error
        };

        var (total, updates, errors) = await BuildOrchestrator(scanner).CheckAsync(records);

        await Assert.That(total).IsEqualTo(3);
        await Assert.That(updates).IsEqualTo(1);
        await Assert.That(errors).IsEqualTo(1);
    }

    [Test]
    public async Task CheckAsync_AppliesLatestVersionBackToRecord()
    {
        var scanner = new FakeScanner
        {
            Name = "S",
            Kind = AppKind.App,
            OnCheck = record =>
            {
                record.App.LatestVersion = "9.9.9";
                return (record, false);
            },
        };
        var record = Record(scanner, "thing", "1.0.0");

        await BuildOrchestrator(scanner).CheckAsync([record]);

        await Assert.That(record.App.LatestVersion).IsEqualTo("9.9.9");
        await Assert.That(record.UpdateAvailable).IsTrue();
    }

    [Test]
    public async Task CheckAsync_RecordsWithoutMatchingScanner_AreNotChecked()
    {
        var present = new FakeScanner { Name = "Present", Kind = AppKind.App };
        var orphanSource = new FakeScanner { Name = "Absent", Kind = AppKind.App };
        var orphan = Record(orphanSource, "lonely", "1.0.0");

        var (total, updates, errors) = await BuildOrchestrator(present).CheckAsync([orphan]);

        await Assert.That(total).IsEqualTo(0);
        await Assert.That(updates).IsEqualTo(0);
        await Assert.That(errors).IsEqualTo(0);
    }

    [Test]
    public async Task CheckAsync_OnlyChecksRecordsMatchingScannerKind()
    {
        var scanner = new FakeScanner { Name = "S", Kind = AppKind.App };
        // Record's kind is DevTool, scanner only handles App → excluded from the group.
        var record = Record(scanner, "tool", "1.0.0", AppKind.DevTool);

        var (total, _, _) = await BuildOrchestrator(scanner).CheckAsync([record]);

        await Assert.That(total).IsEqualTo(0);
    }

    private static AppRecord Record(
        FakeScanner source,
        string name,
        string installed,
        AppKind kind = AppKind.App)
    {
        var app = new DiscoveredApp(source, name, new AppIdentifier(source.Name, source.DisplayName), kind)
        {
            InstalledVersion = installed,
            Attribute = AppAttribute.App,
        };
        return new AppRecord(app);
    }

    private static CheckOrchestrator BuildOrchestrator(params IScanner[] scanners) =>
        new(scanners, new LiveProgressRenderer(scanners), NullLogger<CheckOrchestrator>.Instance);
}
