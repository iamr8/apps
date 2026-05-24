namespace apps.Scanners;

/// <summary>
/// Shared utilities for scanner implementations.
/// </summary>
internal static class ScannerHelper
{
    /// <summary>
    /// Finds an executable by searching the colon-delimited PATH environment variable,
    /// then a set of well-known macOS locations (Homebrew Apple Silicon / Intel, /usr/local,
    /// /usr/bin, /bin). Returns the full path on success, or <c>null</c> if not found.
    /// </summary>
    public static string? FindExecutable(string name)
    {
        // Walk the actual PATH so user-overridden versions are preferred.
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir, name);
            if (File.Exists(full))
            {
                return full;
            }
        }

        // Common macOS locations that may not be in PATH when running inside a .NET
        // process launched from launchd or a non-login shell context.
        string[] fallbacks =
        [
            $"/opt/homebrew/bin/{name}",
            $"/opt/homebrew/sbin/{name}",
            $"/usr/local/bin/{name}",
            $"/usr/local/sbin/{name}",
            $"/usr/bin/{name}",
            $"/usr/sbin/{name}",
            $"/bin/{name}",
            $"/sbin/{name}",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", name)
        ];

        return fallbacks.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="exeName"/> can be located on the system.
    /// Cheap synchronous check suitable for use in <c>IsAvailable()</c>.
    /// </summary>
    public static bool IsExecutableAvailable(string exeName)
    {
        return FindExecutable(exeName) is not null;
    }
}