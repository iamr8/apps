using System.Runtime.CompilerServices;

using apps.Models;

namespace apps.Checkers;

/// <summary>
/// An update-check plugin that handles one UpdateMethod.
/// Each checker is registered with DI and automatically picked up by CheckOrchestrator.
/// </summary>
public interface IUpdateChecker
{
    /// <summary>The update method this checker handles.</summary>
    UpdateMethod Method { get; }

    /// <summary>Human-readable label shown in the <c>Update Method</c> output column (e.g. "Homebrew Cask", "Sparkle").</summary>
    string DisplayName { get; }

    /// <summary>
    /// Source column label and optional qualifier to use when this checker is the active update mechanism.
    /// When <see langword="null"/>, the renderer falls back to the scanner's own source label.
    /// </summary>
    (string Label, string? Qualifier)? SourceOverride => null;

    /// <summary>Returns true when this checker is able to check the given app record.</summary>
    bool CanCheck(AppRecord app);

    /// <summary>Check a single app for an available update.</summary>
    Task<UpdateCheckResult> CheckAsync(AppRecord app, CancellationToken cancellationToken = default);

    /// <summary>
    /// Batch-check multiple apps. Implementations may fan-out concurrently up to
    /// their configured concurrency limit.
    /// </summary>
    Task<IReadOnlyList<UpdateCheckResult>> CheckBatchAsync(IReadOnlyList<AppRecord> apps, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streaming variant used by CheckOrchestrator — yields results as they arrive
    /// rather than waiting for the full batch to complete.
    /// Default implementation drains CheckBatchAsync; override for true streaming.
    /// </summary>
    async IAsyncEnumerable<UpdateCheckResult> CheckStreamAsync(IReadOnlyList<AppRecord> apps, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var result in await CheckBatchAsync(apps, cancellationToken))
        {
            yield return result;
        }
    }
}