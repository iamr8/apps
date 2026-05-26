using System.Runtime.CompilerServices;

using apps.Models;
using apps.Scanners;

using Microsoft.Extensions.Logging;

namespace apps.Components.Windows;

public class WindowsApplicationsScanner(ILogger<WindowsApplicationsScanner> logger)
    : IScanner
{
    private Dictionary<string, bool> _executablePaths = [];

    public string Name => "Applications";

    /// <inheritdoc/>
    public string DisplayName => "Applications";

    public OS SupportedOS => OS.Windows;

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
}