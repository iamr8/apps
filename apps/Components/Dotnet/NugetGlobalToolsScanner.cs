using System.Runtime.CompilerServices;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Dotnet;

/// <summary>
/// Discovers globally installed .NET tools via <c>dotnet tool list -g</c>.
/// Each tool is emitted as <see cref="AppKind.Package"/>.
/// </summary>
public sealed class NugetGlobalToolsScanner(IProcessRunner runner, ILogger<NugetGlobalToolsScanner> logger)
    : IScanner
{
    public string Name => "NuGet";

    /// <inheritdoc/>
    public string DisplayName => "NuGet";

    /// <inheritdoc/>
    public string? GetSourceQualifier(AppKind kind) => "Global";

    public bool IsAvailable()
    {
        return ScannerHelper.IsExecutableAvailable("dotnet");
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var dotnet = ScannerHelper.FindExecutable("dotnet") ?? "dotnet";
        var result = await runner.RunAsync(dotnet, "tool list -g", cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning("'dotnet tool list -g' failed: {Err}", result.StandardError.Trim());
            yield break;
        }

        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Skip the two header lines:
        //   "Package Id   Version   Commands"
        //   "------…"
        var dataLines = lines.SkipWhile(l => l.StartsWith("Package", StringComparison.OrdinalIgnoreCase) ||
                                             l.StartsWith("------", StringComparison.Ordinal));

        foreach (var line in dataLines)
        {
            var app = ParseLine(line);
            if (app is not null)
            {
                yield return app;
            }
        }
    }


    /// <summary>
    /// Line format (variable-width columns, multiple spaces as separator):
    ///   "dotnet-ef                            8.0.4        dotnet-ef"
    /// </summary>
    private static DiscoveredApp? ParseLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;

        var packageId = parts[0];
        var version = parts[1];

        return new DiscoveredApp(
            packageId,
            "NuGet",
            AppKind.Packages,
            version,
            SuggestedMethod: UpdateMethod.PackageRegistry,
            SuggestedMethodDetail: packageId);
    }
}
