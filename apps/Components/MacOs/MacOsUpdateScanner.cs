using System.Runtime.CompilerServices;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

namespace apps.Components.MacOs;

/// <summary>
/// Discovers pending macOS software updates via <c>softwareupdate --list --all</c>.
/// Each item listed is emitted as a <see cref="AppKind.DevTool"/> entry — the fact that
/// they appear at all means an update is available; the <c>MacOsUpdateChecker</c>
/// confirms and records this in Stage 2.
/// </summary>
public sealed class MacOsUpdateScanner(IProcessRunner runner)
    : IScanner
{
    private string? _executablePath;

    public string Name => "macOS";

    /// <inheritdoc/>
    public string DisplayName => "macOS";

    public OS SupportedOS => OS.MacOS;

    /// <summary>Always available on macOS.</summary>
    public bool IsAvailable()
    {
        const string path = "/usr/sbin/softwareupdate";
        if (File.Exists(path))
        {
            _executablePath = path;
            return true;
        }

        return false;
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(_executablePath!, "--list --all", cancellationToken);

        // softwareupdate exits 1 when there are no updates — not an error.
        var output = result.StandardOutput + result.StandardError;

        string? currentLabel = null;
        string? currentVersion = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd();

            // Label lines: "* Label: Name-1.0" or "** Label: Name-1.0 (critical)"
            if (line.TrimStart().StartsWith("* Label:", StringComparison.OrdinalIgnoreCase)
                || line.TrimStart().StartsWith("** Label:", StringComparison.OrdinalIgnoreCase))
            {
                // Flush previous item if any
                if (currentLabel is not null)
                {
                    yield return MakeEntry(currentLabel, currentVersion);
                }

                var labelIdx = line.IndexOf("Label:", StringComparison.OrdinalIgnoreCase);
                currentLabel = line[(labelIdx + "Label:".Length)..].Trim();
                currentVersion = null;
                continue;
            }

            // Title line: "\tTitle: macOS Sequoia 15.5, Version: 15.5, Size: …"
            if (currentLabel is not null && line.Contains("Version:", StringComparison.OrdinalIgnoreCase))
            {
                currentVersion = ExtractVersionFromTitle(line);
            }
        }

        // Flush last item
        if (currentLabel is not null)
        {
            yield return MakeEntry(currentLabel, currentVersion);
        }
    }


    private DiscoveredApp MakeEntry(string label, string? version)
    {
        return new DiscoveredApp(
            label,
            new AppIdentifier(Name, DisplayName),
            AppKind.Packages,
            null, // softwareupdate doesn't report installed version
            SuggestedMethod: UpdateMethod.Specialised,
            SuggestedMethodDetail: version);
    }

    private static string? ExtractVersionFromTitle(string line)
    {
        // "Title: macOS Sequoia 15.5, Version: 15.5, Size: …"
        const string marker = "Version:";
        var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var after = line[(idx + marker.Length)..].TrimStart();
        // Read until the next comma
        var commaIdx = after.IndexOf(',');
        return commaIdx >= 0 ? after[..commaIdx].Trim() : after.Trim();
    }
}