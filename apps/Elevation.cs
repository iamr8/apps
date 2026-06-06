using System.Diagnostics;

namespace apps;

/// <summary>
/// Helpers for filesystem changes that may require administrator privileges. Detects whether a path
/// is writable by the current user and, when it is not, runs the necessary commands through an
/// interactive <c>sudo</c> prompt that owns the terminal.
/// </summary>
internal static class Elevation
{
    /// <summary>
    /// Returns <see langword="true"/> when writing to <paramref name="targetPath"/> needs elevated
    /// privileges. Determined by probing whether the containing directory is writable by the current
    /// user — creating or overwriting the file is a directory operation, so directory write access is
    /// what matters. A missing directory also counts as requiring elevation.
    /// </summary>
    public static bool RequiresElevation(string targetPath)
    {
        var dir = Path.GetDirectoryName(targetPath)!;
        var probePath = Path.Combine(dir, $".apps.permcheck.{Environment.ProcessId}.tmp");

        try
        {
            using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            File.Delete(probePath);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// Prompts for administrator privileges via <c>sudo -v</c>, which validates and caches the
    /// credential so subsequent privileged commands run without a second prompt. Returns
    /// <see langword="true"/> when the credential was granted.
    /// </summary>
    public static Task<bool> TryAcquireSudoAsync(CancellationToken cancellationToken)
    {
        return RunInteractiveAsync("sudo", ["-v"], cancellationToken);
    }

    /// <summary>
    /// Runs a child process with the parent's terminal inherited (no stdio redirection, no timeout) so
    /// interactive prompts such as <c>sudo</c>'s password challenge reach the user. Returns
    /// <see langword="true"/> when the process exits with code 0.
    /// </summary>
    /// <remarks>
    /// This deliberately bypasses <c>ProcessRunner</c>: that runner redirects stdout/stderr and enforces
    /// a 60-second timeout, both of which break an interactive password prompt that must own the TTY.
    /// </remarks>
    public static async Task<bool> RunInteractiveAsync(
        string exe,
        string[] args,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(startInfo);
        if (proc is null)
        {
            return false;
        }

        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return proc.ExitCode == 0;
    }
}
