using System.Runtime.CompilerServices;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.MacOs;

/// <summary>
/// Discovers the installed Xcode version via <c>xcodebuild -version</c>.
/// Emits one <see cref="AppKind.DevTool"/> entry for Xcode itself.
/// </summary>
public sealed class XcodeScanner(IProcessRunner runner, ILogger<XcodeScanner> logger)
    : IScanner
{
    public string Name => "Xcode";

    /// <inheritdoc/>
    public string DisplayName => "Xcode";

    public bool IsAvailable()
    {
        return ScannerHelper.IsExecutableAvailable("xcodebuild");
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var xcodebuild = ScannerHelper.FindExecutable("xcodebuild") ?? "xcodebuild";
        var result = await runner.RunAsync(xcodebuild, "-version", cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning("'xcodebuild -version' failed: {Err}", result.StandardError.Trim());
            yield break;
        }

        // Output:
        //   Xcode 16.3
        //   Build version 16E140
        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var version = ParseXcodeVersion(lines);
        if (version is null)
        {
            yield break;
        }

        // Xcode installs to /Applications/Xcode.app (or Xcode-beta.app)
        var xcodePath = File.Exists("/Applications/Xcode.app")
            ? "/Applications/Xcode.app"
            : null;

        yield return new DiscoveredApp(
            "Xcode",
            Name,
            AppKind.Packages,
            version,
            xcodePath,
            // Xcode updates come through the App Store; do NOT pre-set the method here
            // so the priority chain can assign AppStore if the bundle ID matches.
            SuggestedMethod: null);
    }


    private static string? ParseXcodeVersion(string[] lines)
    {
        foreach (var line in lines)
        {
            // "Xcode 16.3"
            if (line.StartsWith("Xcode ", StringComparison.OrdinalIgnoreCase))
            {
                return line["Xcode ".Length..].Trim();
            }
        }

        return null;
    }
}
