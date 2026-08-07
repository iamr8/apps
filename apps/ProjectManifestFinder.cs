using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;

namespace apps;

/// <summary>
/// Walks the user's home directory (~/) for known project manifest files,
/// respecting .gitignore entries (skips node_modules, .git, etc.).
/// Used by project-level dependency scanners (opt-in via --include-project-deps).
/// </summary>
public sealed class ProjectManifestFinder(ILogger<ProjectManifestFinder> logger)
{
    // Parsed .gitignore directory-name patterns, keyed by the .gitignore file path.
    // A directory's .gitignore is otherwise read once per subdirectory; cache it so it is read once.
    // A null value marks "no .gitignore here" so the miss is not re-probed.
    private readonly ConcurrentDictionary<string, HashSet<string>?> _gitignoreCache = new();

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
    /// Yields manifest file paths under <paramref name="root"/>.
    /// </summary>
    public async IAsyncEnumerable<string> FindAsync(string root, string fileNamePattern, [EnumeratorCancellation] CancellationToken cancellationToken = default)
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

    private async Task<bool> IsGitIgnoredAsync(string dirPath, CancellationToken cancellationToken)
    {
        var dirName = Path.GetFileName(dirPath);
        var parentDir = Path.GetDirectoryName(dirPath);
        if (parentDir is null)
        {
            return false;
        }

        var gitignorePath = Path.Combine(parentDir, ".gitignore");
        var patterns = await GetGitignorePatternsAsync(gitignorePath, cancellationToken);
        return patterns is not null && (patterns.Contains(dirName) || patterns.Contains($"/{dirName}"));
    }

    private async Task<HashSet<string>?> GetGitignorePatternsAsync(string gitignorePath, CancellationToken cancellationToken)
    {
        if (_gitignoreCache.TryGetValue(gitignorePath, out var cached))
        {
            return cached;
        }

        HashSet<string>? patterns = null;
        if (File.Exists(gitignorePath))
        {
            try
            {
                var lines = await File.ReadAllLinesAsync(gitignorePath, cancellationToken);
                patterns = new HashSet<string>(StringComparer.Ordinal);
                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                    {
                        continue;
                    }

                    // Simple exact-match and trailing-slash patterns
                    patterns.Add(line.TrimEnd('/'));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Ignore unreadable .gitignore
                patterns = null;
            }
        }

        _gitignoreCache[gitignorePath] = patterns;
        return patterns;
    }
}