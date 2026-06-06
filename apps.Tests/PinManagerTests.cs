namespace apps.Tests;

/// <summary>
/// Covers <see cref="PinManager"/> load/save round-tripping and the pin-suppression rules.
/// Each test uses a unique temp file via the internal path-injecting constructor, so the real
/// user pin file is never touched.
/// </summary>
public sealed class PinManagerTests
{
    private string _pinFile = null!;

    [Before(Test)]
    public void CreateTempPath()
    {
        _pinFile = Path.Combine(Path.GetTempPath(), $"apps-pins-{Guid.NewGuid():N}.json");
    }

    [After(Test)]
    public void DeleteTempFile()
    {
        if (File.Exists(_pinFile))
        {
            File.Delete(_pinFile);
        }
    }

    [Test]
    public async Task IsPinned_BeforeLoad_ReturnsFalse()
    {
        var manager = new PinManager(_pinFile);
        await Assert.That(manager.IsPinned("anything", "1.0.0")).IsFalse();
    }

    [Test]
    public async Task LoadAsync_MissingFile_LeavesNoPins()
    {
        var manager = new PinManager(_pinFile);
        await manager.LoadAsync();
        await Assert.That(manager.IsPinned("anything", "1.0.0")).IsFalse();
    }

    [Test]
    public async Task PinAsync_ThenIsPinned_AtSameVersion_True()
    {
        var manager = new PinManager(_pinFile);
        await manager.PinAsync("lodash", "1.0.0");

        await Assert.That(manager.IsPinned("lodash", "1.0.0")).IsTrue();
    }

    [Test]
    public async Task PinAsync_VersionSpecific_OnlySuppressesMatchingVersion()
    {
        var manager = new PinManager(_pinFile);
        await manager.PinAsync("lodash", "1.0.0");

        await Assert.That(manager.IsPinned("lodash", "1.0.0")).IsTrue();
        await Assert.That(manager.IsPinned("lodash", "2.0.0")).IsFalse();
    }

    [Test]
    public async Task PinAsync_WithoutVersion_SuppressesAnyVersion()
    {
        var manager = new PinManager(_pinFile);
        await manager.PinAsync("lodash", version: null);

        await Assert.That(manager.IsPinned("lodash", "1.0.0")).IsTrue();
        await Assert.That(manager.IsPinned("lodash", "9.9.9")).IsTrue();
        await Assert.That(manager.IsPinned("lodash", null)).IsTrue();
    }

    [Test]
    public async Task IsPinned_IsCaseInsensitiveOnName()
    {
        var manager = new PinManager(_pinFile);
        await manager.PinAsync("Lodash", "1.0.0");

        await Assert.That(manager.IsPinned("lodash", "1.0.0")).IsTrue();
    }

    [Test]
    public async Task UnpinAsync_RemovesThePin()
    {
        var manager = new PinManager(_pinFile);
        await manager.PinAsync("lodash", "1.0.0");
        await manager.UnpinAsync("lodash");

        await Assert.That(manager.IsPinned("lodash", "1.0.0")).IsFalse();
    }

    [Test]
    public async Task Pins_PersistAcrossInstances()
    {
        var writer = new PinManager(_pinFile);
        await writer.PinAsync("lodash", "1.0.0");
        await writer.PinAsync("react", null);

        var reader = new PinManager(_pinFile);
        await reader.LoadAsync();

        await Assert.That(reader.IsPinned("lodash", "1.0.0")).IsTrue();
        await Assert.That(reader.IsPinned("react", "18.0.0")).IsTrue();
    }
}
