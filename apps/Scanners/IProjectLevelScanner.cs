namespace apps.Scanners;

/// <summary>
/// Marker interface for scanners that discover project-level dependencies.
/// These scanners are only activated when <c>--include-project-deps</c> is passed.
/// </summary>
public interface IProjectLevelScanner : IScanner;