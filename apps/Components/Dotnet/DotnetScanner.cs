using System.Runtime.CompilerServices;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Dotnet;

/// <summary>
/// Discovers all .NET SDKs installed on the system via <c>dotnet --list-sdks</c>.
/// Each installed SDK version is emitted as a separate <see cref="AppKind.Packages"/> entry.
/// </summary>
public sealed class DotnetScanner(IProcessRunner runner, ILogger<DotnetScanner> logger)
    : IScanner
{
    public string Name => "Dotnet";

    /// <inheritdoc/>
    public string DisplayName => ".NET";

    /// <inheritdoc/>
    public string? GetSourceQualifier(AppKind kind) => "SDK";

    public bool IsAvailable()
    {
        return ScannerHelper.IsExecutableAvailable("dotnet");
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var dotnet = ScannerHelper.FindExecutable("dotnet") ?? "dotnet";

        // Output: "8.0.300 [/usr/local/share/dotnet/sdk]"
        var sdkResult = await runner.RunAsync(dotnet, "--list-sdks", cancellationToken);
        if (sdkResult.Success)
        {
            foreach (var line in Lines(sdkResult.StandardOutput))
            {
                var app = ParseSdkLine(line);
                if (app is not null) yield return app;
            }
        }
        else
        {
            logger.LogWarning("'dotnet --list-sdks' failed: {Err}", sdkResult.StandardError.Trim());
        }
    }

    private static DiscoveredApp? ParseSdkLine(string line)
    {
        // "6.0.136 [/usr/local/share/dotnet/sdk]"
        var spaceIdx = line.IndexOf(' ');
        if (spaceIdx < 0)
        {
            return null;
        }

        var version = line[..spaceIdx];
        var basePath = ExtractBracketPath(line);

        return new DiscoveredApp(
            $".NET SDK {MajorMinor(version)}",
            "Dotnet",
            AppKind.Packages,
            version,
            basePath is not null ? Path.Combine(basePath, version) : null,
            SuggestedMethod: UpdateMethod.Sdk);
    }

    private static string? ExtractBracketPath(string line)
    {
        var open = line.LastIndexOf('[');
        var close = line.LastIndexOf(']');
        if (open < 0 || close <= open)
        {
            return null;
        }

        return line[(open + 1)..close];
    }

    /// <summary>
    /// Returns the <c>major.minor</c> segment of a version string so that different
    /// installed SDK generations (e.g. 6.0 and 10.0) get unique names and are not
    /// collapsed by the name-based deduplication in <c>--show-all</c>.
    /// </summary>
    private static string MajorMinor(string version)
    {
        var firstDot = version.IndexOf('.');
        if (firstDot < 0)
        {
            return version;
        }

        var secondDot = version.IndexOf('.', firstDot + 1);
        return secondDot > 0 ? version[..secondDot] : version;
    }

    private static IEnumerable<string> Lines(string output)
    {
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l));
    }
}
