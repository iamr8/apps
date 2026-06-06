namespace apps.Tests;

/// <summary>
/// Covers <see cref="VersionComparer"/> — the heart of update detection. Every "is an update
/// available?" decision in the tool flows through here via <see cref="AppRecord.UpdateAvailable"/>.
/// </summary>
public sealed class VersionComparerTests
{
    [Test]
    [Arguments("1.0.0", "1.0.1")]
    [Arguments("1.0.0", "1.1.0")]
    [Arguments("1.0.0", "2.0.0")]
    [Arguments("1.9.9", "1.10.0")]
    [Arguments("1.0", "1.0.1")]
    public async Task IsNewer_WhenLatestIsHigher_ReturnsTrue(string installed, string latest)
    {
        await Assert.That(VersionComparer.IsNewer(installed, latest)).IsTrue();
    }

    [Test]
    [Arguments("1.0.1", "1.0.0")]
    [Arguments("2.0.0", "1.9.9")]
    [Arguments("1.0.0", "1.0.0")]
    public async Task IsNewer_WhenLatestIsLowerOrEqual_ReturnsFalse(string installed, string latest)
    {
        await Assert.That(VersionComparer.IsNewer(installed, latest)).IsFalse();
    }

    [Test]
    [Arguments(null, "1.0.0")]
    [Arguments("1.0.0", null)]
    [Arguments("", "1.0.0")]
    [Arguments("1.0.0", "   ")]
    [Arguments(null, null)]
    public async Task IsNewer_WhenEitherSideMissing_ReturnsFalse(string? installed, string? latest)
    {
        await Assert.That(VersionComparer.IsNewer(installed, latest)).IsFalse();
    }

    [Test]
    public async Task IsNewer_StripsVPrefix()
    {
        await Assert.That(VersionComparer.IsNewer("v1.0.0", "v1.0.1")).IsTrue();
        await Assert.That(VersionComparer.IsNewer("V1.0.1", "v1.0.0")).IsFalse();
    }

    [Test]
    public async Task IsNewer_UsesOnlyFirstCommaSegment()
    {
        // Homebrew versions can carry a revision after a comma (e.g. "1.2,345").
        await Assert.That(VersionComparer.IsNewer("1.2,999", "1.3,000")).IsTrue();
        await Assert.That(VersionComparer.IsNewer("1.3,000", "1.3,111")).IsFalse();
    }

    [Test]
    [Arguments("1.0.0-alpha", "1.0.0")]
    [Arguments("1.0.0-alpha", "1.0.0-beta")]
    [Arguments("1.0.0-beta", "1.0.0-rc")]
    public async Task Compare_PreReleaseOrdersBelowRelease(string lower, string higher)
    {
        await Assert.That(VersionComparer.Compare(lower, higher)).IsLessThan(0);
        await Assert.That(VersionComparer.Compare(higher, lower)).IsGreaterThan(0);
    }

    [Test]
    public async Task Compare_EqualVersions_ReturnsZero()
    {
        await Assert.That(VersionComparer.Compare("1.2.3", "1.2.3")).IsEqualTo(0);
        await Assert.That(VersionComparer.Compare("v1.2.3", "1.2.3")).IsEqualTo(0);
    }

    [Test]
    public async Task Compare_FourPartVersions()
    {
        await Assert.That(VersionComparer.Compare("1.2.3.4", "1.2.3.5")).IsLessThan(0);
        await Assert.That(VersionComparer.Compare("1.2.3.10", "1.2.3.9")).IsGreaterThan(0);
    }

    [Test]
    public async Task Compare_CompactDateVersions()
    {
        await Assert.That(VersionComparer.Compare("20240101", "20240102")).IsLessThan(0);
        await Assert.That(VersionComparer.Compare("20241231", "20240101")).IsGreaterThan(0);
    }

    [Test]
    public async Task Compare_DottedDateVersions()
    {
        await Assert.That(VersionComparer.Compare("2024.01.01", "2024.02.01")).IsLessThan(0);
    }

    [Test]
    public async Task Compare_NonNumericFallsBackToLexicographic()
    {
        await Assert.That(VersionComparer.Compare("apple", "banana")).IsLessThan(0);
        await Assert.That(VersionComparer.Compare("banana", "apple")).IsGreaterThan(0);
    }

    [Test]
    public async Task Instance_OrdersAsComparer()
    {
        var versions = new[] { "2.0.0", "1.0.0-beta", "1.0.0", "1.5.0" };
        Array.Sort(versions, VersionComparer.Instance);

        await Assert.That(versions).IsEquivalentTo(new[] { "1.0.0-beta", "1.0.0", "1.5.0", "2.0.0" });
    }
}
