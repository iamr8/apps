namespace apps;

/// <summary>ANSI terminal styling helpers. All methods return the input unchanged on non-ANSI terminals.</summary>
internal static class AnsiStyle
{
    /// <summary>True when stdout is connected to a real TTY and <c>NO_COLOR</c> is not set.</summary>
    internal static readonly bool IsAnsi =
        !Console.IsOutputRedirected &&
        Environment.GetEnvironmentVariable("NO_COLOR") is null;

    /// <summary>Applies yellow foreground.</summary>
    internal static string Yellow(string s) => IsAnsi ? $"\e[33m{s}\e[0m" : s;

    /// <summary>Applies green foreground.</summary>
    internal static string Green(string s) => IsAnsi ? $"\e[32m{s}\e[0m" : s;

    /// <summary>Applies red foreground.</summary>
    internal static string Red(string s) => IsAnsi ? $"\e[31m{s}\e[0m" : s;

    /// <summary>Applies cyan foreground.</summary>
    internal static string Cyan(string s) => IsAnsi ? $"\e[36m{s}\e[0m" : s;

    /// <summary>Applies magenta foreground.</summary>
    internal static string Magenta(string s) => IsAnsi ? $"\e[35m{s}\e[0m" : s;

    /// <summary>Applies dim (faint) style.</summary>
    internal static string Dim(string s) => IsAnsi ? $"\e[2m{s}\e[0m" : s;

    /// <summary>Applies dark-gray (bright-black) foreground.</summary>
    internal static string DarkGray(string s) => IsAnsi ? $"\e[90m{s}\e[0m" : s;

    /// <summary>Applies bold style.</summary>
    internal static string Bold(string s) => IsAnsi ? $"\e[1m{s}\e[0m" : s;

    /// <summary>Truncates <paramref name="s"/> to at most <paramref name="max"/> visible characters, appending '…' when cut.</summary>
    internal static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>
    /// Builds a styled progress bar string: <c>[████░░░░░░] 40%</c>.
    /// The bar is <paramref name="width"/> characters wide (excluding brackets and percentage).
    /// </summary>
    internal static string ProgressBar(int completed, int total, int width = 20)
    {
        if (total <= 0)
        {
            total = 1;
        }

        var fraction = Math.Clamp((double)completed / total, 0.0, 1.0);
        var filled = (int)(fraction * width);
        var empty = width - filled;
        var pct = (int)(fraction * 100);

        var filledStr = new string(OperatingSystem.IsWindows() ? '█' : '━', filled);
        var emptyStr = new string('─', empty);

        if (!IsAnsi)
        {
            return $"[{filledStr}{emptyStr}] {pct,3}%";
        }

        return $"{Cyan("[")}{Green(filledStr)}{DarkGray(emptyStr)}{Cyan("]")} {Bold($"{pct,3}%")}";
    }
}
