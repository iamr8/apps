using System.Text.Json;

namespace apps.Components.JetBrains;

/// <summary>
/// Resolves the build number of every installed JetBrains IDE by locating its
/// <c>product-info.json</c> and joining on <c>dataDirectoryName</c> — the same value that
/// names the IDE's config directory under <c>~/Library/Application Support/JetBrains/</c>.
/// </summary>
/// <remarks>
/// The build is needed so plugin update checks only report versions compatible with the
/// installed IDE. Without it the marketplace returns the absolute latest version, which may
/// target a newer IDE build than the user has and therefore is not actually installable.
/// </remarks>
internal static class JetBrainsBuildResolver
{
    /// <summary>
    /// Builds a map from IDE data-directory name (e.g. <c>Rider2026.2</c>) to its full build
    /// string (e.g. <c>RD-262.8665.400</c>). Unreadable or malformed products are skipped.
    /// </summary>
    public static Dictionary<string, string> ResolveBuilds()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in EnumerateProductInfoFiles())
        {
            JetBrainsProductInfo? info;
            try
            {
                using var stream = File.OpenRead(file);
                info = JsonSerializer.Deserialize(stream, JetBrainsJsonContext.Default.JetBrainsProductInfo);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                continue;
            }

            if (info?.DataDirectoryName is { Length: > 0 } dataDir
                && info.ProductCode is { Length: > 0 } code
                && info.BuildNumber is { Length: > 0 } build)
            {
                map[dataDir] = $"{code}-{build}";
            }
        }

        return map;
    }

    private static IEnumerable<string> EnumerateProductInfoFiles()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsMacOS())
        {
            foreach (var appsRoot in new[] { "/Applications", Path.Join(home, "Applications") })
            {
                foreach (var bundle in SafeEnumerateDirectories(appsRoot, "*.app"))
                {
                    var candidate = Path.Join(bundle, "Contents", "Resources", "product-info.json");
                    if (File.Exists(candidate))
                    {
                        yield return candidate;
                    }
                }
            }

            var toolbox = Path.Join(home, "Library", "Application Support", "JetBrains", "Toolbox", "apps");
            foreach (var file in SafeEnumerateFiles(toolbox, "product-info.json"))
            {
                yield return file;
            }

            yield break;
        }

        if (OperatingSystem.IsWindows())
        {
            var roots = new[]
            {
                Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "JetBrains"),
                Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "JetBrains"),
                Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JetBrains", "Toolbox", "apps")
            };

            foreach (var root in roots)
            {
                foreach (var file in SafeEnumerateFiles(root, "product-info.json"))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root, string pattern)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(root, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }
}
