namespace apps;

/// <summary>
/// Abstraction over subprocess execution — mockable in tests.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="exe"/> with <paramref name="args"/>, captures stdout/stderr,
    /// and returns the result. Concurrent invocations are throttled to a global cap of 6.
    /// </summary>
    Task<ProcessResult> RunAsync(string exe, string args, CancellationToken cancellationToken = default);

    /// <summary>
    /// Convenience overload — returns stdout on success, or throws on non-zero exit.
    /// </summary>
    Task<string> ReadOutputAsync(string exe, string args, CancellationToken cancellationToken = default);
}