using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Chocolatey;

/// <summary>
/// Discovers packages installed via Chocolatey (<c>choco list</c>).
/// Gracefully skips when <c>choco</c> is not found on the system.
/// Each discovered package is emitted as <see cref="AppKind.Packages"/> with
/// <see cref="UpdateMethod.Chocolatey"/> pre-assigned.
/// </summary>
public sealed class ChocoScanner(IProcessRunner runner, ILogger<ChocoScanner> logger)
    : IScanner
{
    private string? _executablePath;

    /// <inheritdoc/>
    public string Name => "Chocolatey";

    /// <inheritdoc/>
    public string DisplayName => "Chocolatey";

    public OS SupportedOS => OS.MacOS | OS.Windows;

    public bool IsAvailable()
    {
        _executablePath = ScannerHelper.FindExecutable("choco");
        return _executablePath is not null;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ProcessResult result;
        try
        {
            result = await runner.RunAsync(_executablePath!, "list", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to run 'choco list'");
            yield break;
        }

        if (!result.Success)
        {
            logger.LogWarning("'choco list' failed ({Code}): {Err}", result.ExitCode, result.StandardError.Trim());
            yield break;
        }

        foreach (var line in SplitLines(result.StandardOutput))
        {
            var app = ParseLine(line);

            if (app is not null)
            {
                yield return app;
            }
        }
    }

    /// <summary>
    /// Parses a single <c>choco list</c> output line: <c>packagename version</c>.
    /// Header and summary lines are filtered by <see cref="SplitLines"/>.
    /// </summary>
    private DiscoveredApp? ParseLine(string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 1 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return null;
        }

        var name = parts[0].Trim();
        var version = parts.Length >= 2 ? parts[1].Trim() : null;

        return new DiscoveredApp(
            name,
            new AppIdentifier(Name, DisplayName, "Application"),
            AppKind.Packages,
            version,
            SuggestedMethod: UpdateMethod.Chocolatey,
            SuggestedMethodDetail: name);
    }

    private static IEnumerable<string> SplitLines(string output)
    {
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l =>
                !string.IsNullOrWhiteSpace(l)
                && !l.StartsWith("Chocolatey", StringComparison.OrdinalIgnoreCase)
                && !l.Contains("packages installed", StringComparison.OrdinalIgnoreCase));
    }
}