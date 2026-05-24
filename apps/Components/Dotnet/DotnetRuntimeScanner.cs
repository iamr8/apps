using System.Runtime.CompilerServices;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Dotnet;

/// <summary>
/// Discovers all .NET runtimes installed on the system via <c>dotnet --list-runtimes</c>.
/// Each installed runtime version is emitted as a separate <see cref="AppKind.Packages"/> entry.
/// </summary>
public sealed class DotnetRuntimeScanner(IProcessRunner runner, ILogger<DotnetRuntimeScanner> logger)
    : IScanner
{
    /// <inheritdoc/>
    public string Name => "DotnetRuntime";

    /// <inheritdoc/>
    public string DisplayName => ".NET";

    /// <inheritdoc/>
    public string? GetSourceQualifier(AppKind kind) => "Runtime";

    /// <inheritdoc/>
    public bool IsAvailable()
    {
        return ScannerHelper.IsExecutableAvailable("dotnet");
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var dotnet = ScannerHelper.FindExecutable("dotnet") ?? "dotnet";

        var rtResult = await runner.RunAsync(dotnet, "--list-runtimes", cancellationToken);
        if (rtResult.Success)
        {
            foreach (var line in Lines(rtResult.StandardOutput))
            {
                var app = ParseRuntimeLine(line);
                if (app is not null)
                {
                    yield return app;
                }
            }
        }
        else
        {
            logger.LogWarning("'dotnet --list-runtimes' failed: {Err}", rtResult.StandardError.Trim());
        }
    }

    private static DiscoveredApp? ParseRuntimeLine(string line)
    {
        // "Microsoft.NETCore.App 8.0.5 [/usr/local/share/dotnet/shared/Microsoft.NETCore.App]"
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var runtimeName = parts[0];
        var version = parts[1];
        var basePath = ExtractBracketPath(line);

        return new DiscoveredApp(
            $"{FriendlyName(runtimeName)} {MajorMinor(version)}",
            "DotnetRuntime",
            AppKind.Packages,
            version,
            basePath is not null ? Path.Combine(basePath, version) : null,
            SuggestedMethod: UpdateMethod.Sdk,
            SuggestedMethodDetail: runtimeName);
    }

    /// <summary>
    /// Maps the raw runtime framework name to a human-friendly display name.
    /// </summary>
    private static string FriendlyName(string runtimeName) => runtimeName switch
    {
        "Microsoft.NETCore.App" => ".NET Runtime",
        "Microsoft.AspNetCore.App" => "ASP.NET Core Runtime",
        "Microsoft.WindowsDesktop.App" => ".NET Desktop Runtime",
        _ => runtimeName
    };

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
    /// Returns the <c>major.minor</c> segment of a version string.
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

