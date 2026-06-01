namespace apps.Models;

/// <summary>
/// Output of an update checker for a single app.
/// </summary>
public sealed record UpdateCheckResult
{
    /// <summary>
    /// Output of an update checker for a single app.
    /// </summary>
    public UpdateCheckResult(DiscoveredApp App,
        UpdateMethod CheckMethod,
        bool UpdateAvailable,
        string? InstalledVersion,
        string? LatestVersion,
        string? Error = null)
    {
        this.App = App;
        this.CheckMethod = CheckMethod;
        this.UpdateAvailable = UpdateAvailable;
        this.InstalledVersion = InstalledVersion;
        this.LatestVersion = LatestVersion;
        this.Error = Error;
    }

    public UpdateCheckResult(DiscoveredApp App,
        string error)
    {
        this.App = App;
        this.Error = error;
    }

    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True when the check succeeded without an error.</summary>
    public bool IsSuccess => Error is null;

    public DiscoveredApp App { get; init; }
    public UpdateMethod CheckMethod { get; init; }
    public bool UpdateAvailable { get; init; }
    public string? InstalledVersion { get; init; }
    public string? LatestVersion { get; init; }
    public string? Error { get; init; }
}