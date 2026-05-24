using apps.Models;

namespace apps.Scanners;

/// <summary>
/// A discovery plugin that finds installed applications, runtimes, or packages.
/// Each scanner is registered with DI and automatically picked up by ScanOrchestrator.
/// </summary>
public interface IScanner
{
    /// <summary>Human-readable name used for logging and --source filtering (e.g. "Homebrew", "AppStore").</summary>
    string Name { get; }

    /// <summary>Human-readable label shown in the <c>Source</c> output column (e.g. "App Store", ".NET").</summary>
    string DisplayName { get; }

    /// <summary>
    /// Optional qualifier shown in parentheses in the <c>Source</c> column (e.g. "Image", "Global").
    /// The <paramref name="kind"/> parameter allows kind-dependent qualifiers (e.g. "Extension" only for <see cref="AppKind.Extension"/>).
    /// Returns <see langword="null"/> by default — no qualifier is appended.
    /// </summary>
    string? GetSourceQualifier(AppKind kind) => null;


    /// <summary>
    /// When <see langword="true"/>, a colon-delimited version tag is stripped from the display
    /// name before rendering (e.g. Docker <c>repo:tag</c> → <c>repo</c>).
    /// </summary>
    bool StripTagFromDisplayName => false;

    /// <summary>
    /// Quickly check whether this scanner can run in the current environment
    /// (e.g. is the required CLI tool installed?).
    /// Called once before each scan run; false = scanner is silently skipped.
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// Yields discovered apps as they are found.
    /// Implementations MUST be async-streaming — write to the channel and yield;
    /// do not buffer the entire result set before returning.
    /// </summary>
    IAsyncEnumerable<DiscoveredApp> ScanAsync(CancellationToken cancellationToken = default);
}