namespace apps.Scanners;

/// <summary>
/// Shared utilities for scanner implementations.
/// </summary>
internal static class ScannerHelper
{
    private static readonly Dictionary<string, string[]> WindowsFallbacks;
    private static readonly EnumerationOptions WindowsEnumerationOptions;

    static ScannerHelper()
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsFallbacks ??= new Dictionary<string, string[]>();
            WindowsFallbacks.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Programs"), []);
            WindowsFallbacks.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming"), []);
            WindowsFallbacks.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)), []);
            WindowsFallbacks.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)), []);

            WindowsEnumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                IgnoreInaccessible = true,
            };

            foreach (var fallback in WindowsFallbacks.Keys)
            {
                var files = Directory.EnumerateFiles(fallback, "*.exe", WindowsEnumerationOptions).ToArray();
                WindowsFallbacks[fallback] = files;
            }
        }
    }

    /// <summary>
    /// Finds an executable by searching the colon-delimited PATH environment variable,
    /// then a set of well-known macOS locations (Homebrew Apple Silicon / Intel, /usr/local,
    /// /usr/bin, /bin). Returns the full path on success, or <c>null</c> if not found.
    /// </summary>
    public static string? FindExecutable(string name)
    {
        // Walk the actual PATH so user-overridden versions are preferred.
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";

        if (OperatingSystem.IsMacOS())
        {
            var executableName = name;
            foreach (var dir in pathVar.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                var full = Path.Combine(dir, executableName);
                if (File.Exists(full))
                {
                    return full;
                }
            }

            // Common macOS locations that may not be in PATH when running inside a .NET
            // process launched from launchd or a non-login shell context.
            string[] fallbacks =
            [
                $"/opt/homebrew/bin/{executableName}",
                $"/opt/homebrew/sbin/{executableName}",
                $"/usr/local/bin/{executableName}",
                $"/usr/local/sbin/{executableName}",
                $"/usr/bin/{executableName}",
                $"/usr/sbin/{executableName}",
                $"/bin/{executableName}",
                $"/sbin/{executableName}",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", executableName)
            ];

            return fallbacks.FirstOrDefault(File.Exists);
        }
        else if (OperatingSystem.IsWindows())
        {
            var executableName = Path.HasExtension(name) ? name : $"{name}.exe";
            foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var full = Path.Combine(dir, executableName);
                if (File.Exists(full))
                {
                    return full;
                }
            }

            foreach (var fallback in WindowsFallbacks)
            {
                if (!Directory.Exists(fallback.Key))
                {
                    continue;
                }

                var full = fallback.Value.FirstOrDefault(file => Path.GetFileName(file).Equals(executableName, StringComparison.OrdinalIgnoreCase));
                if (full is not null)
                {
                    return full;
                }
            }

            return null;
        }
        else
        {
            throw new PlatformNotSupportedException("Unsupported OS");
        }
    }
}