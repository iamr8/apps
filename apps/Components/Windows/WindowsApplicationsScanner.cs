using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;

namespace apps.Components.Windows;

#pragma warning disable CS9113 // Parameter is unread.
public class WindowsApplicationsScanner(ILogger<WindowsApplicationsScanner> logger)
#pragma warning restore CS9113 // Parameter is unread.
    : IScanner
{
    private Dictionary<string, bool> _executablePaths = [];

    public string Name => "Applications";

    /// <inheritdoc/>
    public string DisplayName => "Applications";

    public OS SupportedOS => OS.Windows;
    public AppKind Kind => AppKind.App;

    /// <inheritdoc/>
    public bool IsAvailable()
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield break;
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<(AppRecord App, bool Error)> CheckAsync(AppRecord[] apps, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}