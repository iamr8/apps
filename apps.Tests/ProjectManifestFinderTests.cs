using Microsoft.Extensions.Logging.Abstractions;

namespace apps.Tests;

/// <summary>
/// Covers <see cref="ProjectManifestFinder.FindAsync"/> directory-walk rules: well-known build
/// dirs and hidden dirs are skipped, and <c>.gitignore</c>'d directories are excluded.
/// Runs against a throwaway temp tree.
/// </summary>
public sealed class ProjectManifestFinderTests
{
    private string _root = null!;

    [Before(Test)]
    public void CreateTree()
    {
        _root = Path.Combine(Path.GetTempPath(), $"apps-finder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [After(Test)]
    public void DeleteTree()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task FindAsync_FindsManifestsInNormalDirectories()
    {
        Manifest("pkgA");
        Manifest("nested/pkgB");

        var found = await FindAll();

        await Assert.That(found).Contains(Path.Combine(_root, "pkgA", "package.json"));
        await Assert.That(found).Contains(Path.Combine(_root, "nested", "pkgB", "package.json"));
    }

    [Test]
    public async Task FindAsync_SkipsWellKnownBuildDirectories()
    {
        Manifest("node_modules/dep");
        Manifest("bin");
        Manifest("obj");
        Manifest("real");

        var found = await FindAll();

        await Assert.That(found).Contains(Path.Combine(_root, "real", "package.json"));
        await Assert.That(found.Any(p => p.Contains("node_modules"))).IsFalse();
        await Assert.That(found.Any(p => p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))).IsFalse();
        await Assert.That(found.Any(p => p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))).IsFalse();
    }

    [Test]
    public async Task FindAsync_SkipsHiddenDirectories()
    {
        Manifest(".hidden");
        Manifest("visible");

        var found = await FindAll();

        await Assert.That(found).Contains(Path.Combine(_root, "visible", "package.json"));
        await Assert.That(found.Any(p => p.Contains(".hidden"))).IsFalse();
    }

    [Test]
    public async Task FindAsync_SkipsGitignoredDirectories()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "secret/\n");
        Manifest("secret");
        Manifest("public");

        var found = await FindAll();

        await Assert.That(found).Contains(Path.Combine(_root, "public", "package.json"));
        await Assert.That(found.Any(p => p.Contains("secret"))).IsFalse();
    }

    [Test]
    public async Task FindAsync_MatchesOnlyRequestedPattern()
    {
        Manifest("pkg");
        File.WriteAllText(Path.Combine(_root, "pkg", "other.txt"), "x");

        var found = await FindAll();

        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0]).EndsWith("package.json");
    }

    private void Manifest(string relativeDir)
    {
        var dir = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), "{}");
    }

    private async Task<List<string>> FindAll()
    {
        var finder = new ProjectManifestFinder(NullLogger<ProjectManifestFinder>.Instance);
        var results = new List<string>();
        await foreach (var path in finder.FindAsync(_root, "package.json"))
        {
            results.Add(path);
        }

        return results;
    }
}
