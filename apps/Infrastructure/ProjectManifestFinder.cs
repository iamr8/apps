using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;

namespace apps.Infrastructure;

/// <summary>
/// Walks the user's home directory (~/) for known project manifest files,
/// respecting .gitignore entries (skips node_modules, .git, etc.).
/// Used by project-level dependency scanners (opt-in via --include-project-deps).
/// </summary>
public sealed class ProjectManifestFinder(ILogger<ProjectManifestFinder> logger)
{
    // Well-known directories to always skip regardless of .gitignore
    private static readonly HashSet<string> AlwaysSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", ".hg",
        "bin", "obj", // .NET build output
        ".nuget",
        "__pycache__",
        ".tox",
        "vendor", // Go / Ruby
        ".bundle",
        "Pods", // CocoaPods
        ".gradle",
        "build",
        "dist",
        "out",
        ".cache",
        ".terraform"
    };

    /// <summary>
    /// Yields the absolute paths of all manifest files that match
    /// <paramref name="fileNamePattern"/> under the user's home directory.
    /// </summary>
    public IAsyncEnumerable<string> FindAsync(string fileNamePattern, CancellationToken cancellationToken = default)
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return WalkAsync(homeDir, fileNamePattern, cancellationToken);
    }

    /// <summary>
    /// Yields manifest file paths under <paramref name="root"/>.
    /// </summary>
    public async IAsyncEnumerable<string> WalkAsync(string root, string fileNamePattern, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dir = stack.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, fileNamePattern, SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                logger.LogDebug("Skipping inaccessible dir: {Dir}", dir);
                continue;
            }

            foreach (var f in files)
            {
                yield return f;
            }

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(dir, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var sub in subDirs)
            {
                var name = Path.GetFileName(sub);
                if (AlwaysSkip.Contains(name))
                {
                    continue;
                }

                // Skip hidden dirs (except the home dir itself) and symlinks
                if (name.StartsWith('.') && sub != root)
                {
                    continue;
                }

                var info = new DirectoryInfo(sub);
                if (info.LinkTarget is not null)
                {
                    continue; // skip symlinks to avoid loops
                }

                if (await IsGitIgnoredAsync(sub, cancellationToken))
                {
                    continue;
                }

                stack.Push(sub);
            }
        }
    }

    private static async Task<bool> IsGitIgnoredAsync(string dirPath, CancellationToken cancellationToken)
    {
        var dirName = Path.GetFileName(dirPath);
        var parentDir = Path.GetDirectoryName(dirPath);
        if (parentDir is null)
        {
            return false;
        }

        var gitignorePath = Path.Combine(parentDir, ".gitignore");
        if (!File.Exists(gitignorePath))
        {
            return false;
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(gitignorePath, cancellationToken);
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                {
                    continue;
                }

                // Simple exact-match and trailing-slash patterns
                var pattern = line.TrimEnd('/');
                if (pattern == dirName || pattern == $"/{dirName}")
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore unreadable .gitignore
        }

        return false;
    }
}