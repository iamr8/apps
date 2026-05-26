using System.Runtime.CompilerServices;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.AppStore;

/// <summary>
/// Discovers all App Store applications installed via the <c>mas</c> CLI.
/// Each app is emitted as <see cref="AppKind.App"/> with
/// <see cref="UpdateMethod.AppStore"/> and its Apple numeric ID stored as
/// <see cref="DiscoveredApp.SuggestedMethodDetail"/> for use by <c>AppStoreChecker</c>.
/// </summary>
public sealed class AppStoreScanner(IProcessRunner runner, ILogger<AppStoreScanner> logger)
    : IScanner
{
    private string? _executablePath;

    public string Name => "AppStore";

    /// <inheritdoc/>
    public string DisplayName => "App Store";

    public OS SupportedOS => OS.MacOS;

    public bool IsAvailable()
    {
        _executablePath = ScannerHelper.FindExecutable("mas");
        return _executablePath is not null;
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(_executablePath!, "list", cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning("'mas list' failed ({Code}): {Err}", result.ExitCode, result.StandardError.Trim());
            yield break;
        }

        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var app = ParseLine(line);
            if (app is not null)
            {
                logger.LogDebug("AppStore: {Name} ({Id}) v{Ver}", app.Name, app.SuggestedMethodDetail, app.InstalledVersion);
                yield return app;
            }
        }
    }

    /// <summary>
    /// mas list format: "APPID   App Name With Spaces   1.2.3"
    /// The first token is the numeric Apple ID, the last token is the version,
    /// everything in between is the display name.
    /// </summary>
    private DiscoveredApp? ParseLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        // First part must be purely numeric (Apple ID)
        if (!long.TryParse(parts[0], out _))
        {
            return null;
        }

        var appleId = parts[0];
        var version = parts[^1];
        var name = string.Join(' ', parts[1..^1]).Trim();

        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return new DiscoveredApp(
            name,
            new AppIdentifier(Name, DisplayName, "Application"),
            AppKind.App,
            version,
            SuggestedMethod: UpdateMethod.AppStore,
            SuggestedMethodDetail: appleId);
    }
}