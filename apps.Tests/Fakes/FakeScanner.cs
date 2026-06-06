using System.Runtime.CompilerServices;

namespace apps.Tests.Fakes;

/// <summary>
/// Configurable <see cref="IScanner"/> for orchestrator tests. Yields a pre-built list of apps
/// from <see cref="ScanAsync"/> and applies an optional per-record transform in <see cref="CheckAsync"/>.
/// </summary>
public sealed class FakeScanner : IScanner
{
    private readonly List<DiscoveredApp> _apps = [];

    public required string Name { get; init; }

    public string DisplayName => Name;

    public OS SupportedOS { get; init; } = OS.MacOS | OS.Windows;

    public AppKind Kind { get; init; } = AppKind.App;

    public bool Available { get; init; } = true;

    /// <summary>Optional transform applied to each record during <see cref="CheckAsync"/>; defaults to a no-op success.</summary>
    public Func<AppRecord, (AppRecord App, bool Error)>? OnCheck { get; init; }

    public bool IsAvailable() => Available;

    /// <summary>Adds an app to this scanner's discovery output, sourced to this scanner. Returns the scanner for chaining.</summary>
    public FakeScanner Add(
        string name,
        AppKind? kind = null,
        string? installedVersion = null,
        AppAttribute attribute = AppAttribute.App)
    {
        _apps.Add(new DiscoveredApp(this, name, new AppIdentifier(Name, DisplayName), kind ?? Kind)
        {
            InstalledVersion = installedVersion,
            Attribute = attribute,
        });
        return this;
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var app in _apps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return app;
        }

        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<(AppRecord App, bool Error)> CheckAsync(
        AppRecord[] apps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var record in apps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return OnCheck?.Invoke(record) ?? (record, false);
        }

        await Task.CompletedTask;
    }
}
