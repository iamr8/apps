using System.Runtime.CompilerServices;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.MacPorts;

/// <summary>
/// Discovers all ports installed via MacPorts (<c>port installed</c>).
/// Only emits ports that are marked <c>(active)</c>.
/// </summary>
public sealed class MacPortsScanner(IProcessRunner runner, ILogger<MacPortsScanner> logger)
    : IScanner
{
    private string? _executablePath;

    public string Name => "MacPorts";

    /// <inheritdoc/>
    public string DisplayName => "MacPorts";

    public OS SupportedOS => OS.MacOS;

    public bool IsAvailable()
    {
        var exe = ScannerHelper.FindExecutable("port") ?? (File.Exists("/opt/local/bin/port") ? "/opt/local/bin/port" : null);
        if (exe is null)
        {
            return false;
        }

        // Run `port version` synchronously to verify MacPorts can initialise.
        // After an OS upgrade, the binary exists but port fails with a platform-mismatch
        // error until the user runs the migration — detecting this here prevents a loud
        // warning during every scan.
        try
        {
            using var probe = new System.Diagnostics.Process();
            probe.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = "version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            probe.Start();
            var exited = probe.WaitForExit(3_000);
            if (exited && probe.ExitCode == 0)
            {
                _executablePath = exe;
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(_executablePath!, "installed", cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning("'port installed' failed ({Code}): {Err}", result.ExitCode, result.StandardError.Trim());
            yield break;
        }

        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var app = ParseLine(line);
            if (app is not null)
            {
                yield return app;
            }
        }
    }


    /// <summary>
    /// Port line format: "  portname @1.2.3_0 (active)"
    /// Multiple installed versions may appear; only the active one is emitted.
    /// </summary>
    private DiscoveredApp? ParseLine(string line)
    {
        // Only track the active version
        if (!line.Contains("(active)", StringComparison.Ordinal))
        {
            return null;
        }

        var trimmed = line.Trim();
        // Split on whitespace: ["portname", "@1.2.3_0", "(active)"]
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var name = parts[0];
        // Strip the leading '@' and the trailing '_N' revision suffix
        var atVer = parts[1]; // e.g. "@1.2.3_0"
        if (!atVer.StartsWith('@'))
        {
            return null;
        }

        var withoutAt = atVer[1..]; // "1.2.3_0"
        var underIdx = withoutAt.LastIndexOf('_');
        var version = underIdx > 0 ? withoutAt[..underIdx] : withoutAt;

        return new DiscoveredApp(
            name,
            new AppIdentifier(Name, DisplayName, "Application"),
            AppKind.Packages,
            version,
            SuggestedMethod: UpdateMethod.MacPorts,
            SuggestedMethodDetail: name);
    }
}