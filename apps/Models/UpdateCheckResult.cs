namespace apps.Models;

/// <summary>
/// Output of an update checker for a single app.
/// </summary>
public sealed record UpdateCheckResult(
    string AppName,
    UpdateMethod CheckMethod,
    bool UpdateAvailable,
    string? InstalledVersion,
    string? LatestVersion,
    string? Error = null
)
{
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True when the check succeeded without an error.</summary>
    public bool IsSuccess => Error is null;
}