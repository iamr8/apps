namespace apps;

/// <summary>
/// Options passed from <see cref="UpdateCommand"/> to <see cref="Orchestrator"/>.
/// </summary>
public sealed class PipelineOptions
{
    /// <summary>Restrict the pipeline to a single app kind. <c>null</c> means all kinds.</summary>
    public AppKind? ScopeKind { get; init; }

    /// <summary>
    /// When <c>true</c>, show all apps regardless of update status.
    /// When <c>false</c> (default), show only apps with an available update.
    /// </summary>
    public bool ShowAll { get; init; }

    /// <summary>
    /// When <c>true</c>, only scan and display discovered apps without checking for updates.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Package name to pin at its current version. <c>null</c> means no pin action.
    /// </summary>
    public string? PinPackage { get; init; }

    /// <summary>
    /// Package name to unpin. <c>null</c> means no unpin action.
    /// </summary>
    public string? UnpinPackage { get; init; }
}