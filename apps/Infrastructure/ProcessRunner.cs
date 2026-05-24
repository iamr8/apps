using System.Diagnostics;

using Microsoft.Extensions.Logging;

namespace apps.Infrastructure;

/// <summary>
/// Runs subprocesses with a global concurrency cap of 6 to avoid overloading the system.
/// stdout and stderr are read concurrently with WaitForExitAsync to prevent deadlocks
/// when a child fills its pipe buffer.
/// Each subprocess is killed after <see cref="DefaultTimeout"/> if it has not exited.
/// </summary>
public sealed class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
    // Global cap: never run more than 6 subprocesses simultaneously
    private static readonly SemaphoreSlim Cap = new(6, 6);

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public async Task<ProcessResult> RunAsync(string exe, string args, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Running: {Exe} {Args}", exe, args);

        await Cap.WaitAsync(cancellationToken);
        try
        {
            using var proc = new Process();
            proc.StartInfo = BuildStartInfo(exe, args);
            proc.Start();

            // Link the caller's token with a timeout so hung subprocesses are killed.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DefaultTimeout);
            var linked = timeoutCts.Token;

            // Read stdout + stderr concurrently with WaitForExitAsync.
            // WITHOUT concurrency the child can fill its pipe buffer and deadlock.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(linked);
            var stderrTask = proc.StandardError.ReadToEndAsync(linked);

            try
            {
                await proc.WaitForExitAsync(linked);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout fired, not caller cancellation — kill the hung process.
                TryKill(proc);
                logger.LogWarning("{Exe} {Args} killed after {Timeout}s timeout",
                    exe, args, DefaultTimeout.TotalSeconds);
                return new ProcessResult(-1, string.Empty, $"Process timed out after {DefaultTimeout.TotalSeconds}s");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            logger.LogDebug(
                "{Exe} exited {Code}; stdout={StdoutLen} chars stderr={StderrLen} chars",
                exe, proc.ExitCode, stdout.Length, stderr.Length);

            return new ProcessResult(proc.ExitCode, stdout, stderr);
        }
        finally
        {
            Cap.Release();
        }
    }

    public async Task<string> ReadOutputAsync(string exe, string args, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(exe, args, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException($"`{exe} {args}` exited {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }

    private static readonly string SafeWorkingDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static ProcessStartInfo BuildStartInfo(string exe, string args)
    {
        return new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = SafeWorkingDirectory
        };
    }

    private static void TryKill(Process proc)
    {
        try
        {
            proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort: process may have already exited between the timeout and kill.
        }
    }
}