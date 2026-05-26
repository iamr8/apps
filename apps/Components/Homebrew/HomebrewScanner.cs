using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Homebrew;

/// <summary>
/// Discovers all Homebrew formulas and casks installed on the system.
/// Formulas are emitted as <see cref="AppKind.Packages"/>; casks as <see cref="AppKind.App"/>.
/// Descriptions are fetched from <c>brew info --json=v2 --installed</c> and stored
/// in <see cref="DiscoveredApp.Description"/> for dim-subtitle display.
/// </summary>
public sealed class HomebrewScanner(IProcessRunner runner, ILogger<HomebrewScanner> logger)
    : IScanner
{
    private string? _executablePath;

    public string Name => "Homebrew";

    /// <inheritdoc/>
    public string DisplayName => "Homebrew";

    public OS SupportedOS => OS.MacOS;

    public bool IsAvailable()
    {
        string[] candidates =
        [
            "/opt/homebrew/bin/brew",
            "/usr/local/bin/brew"
        ];
        _executablePath = candidates.FirstOrDefault(File.Exists);
        return _executablePath is not null;
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Run all three commands concurrently — descriptions are a bonus and failures are non-fatal.
        var formulaTask = runner.RunAsync(_executablePath!, "list --versions", cancellationToken);
        var caskTask = runner.RunAsync(_executablePath!, "list --cask --versions", cancellationToken);
        var infoTask = runner.RunAsync(_executablePath!, "info --json=v2 --installed", cancellationToken);

        var formulaResult = await formulaTask.ConfigureAwait(false);
        var caskResult = await caskTask.ConfigureAwait(false);
        var infoResult = await infoTask.ConfigureAwait(false);

        var (descriptions, displayNames) = ParseBrewInfo(infoResult);

        // Build cask name set first — cask supersedes formula when both share the same name
        // (e.g. git-credential-manager ships as both; the cask is the GUI app with a higher-
        // priority update channel: HomebrewCask(2) > HomebrewFormula(3)).
        var caskNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (caskResult.Success)
        {
            foreach (var line in Split(caskResult.StandardOutput))
            {
                var firstSpace = line.IndexOf(' ');
                var token = firstSpace > 0 ? line[..firstSpace] : line;
                if (!string.IsNullOrWhiteSpace(token))
                {
                    caskNames.Add(token);
                }
            }
        }
        else
        {
            logger.LogWarning("'brew list --cask --versions' failed: {Err}", caskResult.StandardError.Trim());
        }

        if (formulaResult.Success)
        {
            foreach (var line in Split(formulaResult.StandardOutput))
            {
                var app = ParseFormulaLine(line, descriptions);
                if (app is not null && !caskNames.Contains(app.Name))
                {
                    yield return app;
                }
            }
        }
        else
        {
            logger.LogWarning("'brew list --versions' failed: {Err}", formulaResult.StandardError.Trim());
        }

        if (caskResult.Success)
        {
            foreach (var line in Split(caskResult.StandardOutput))
            {
                var app = ParseCaskLine(line, descriptions, displayNames);
                if (app is not null)
                {
                    yield return app;
                }
            }
        }
    }

    /// <summary>
    /// Formula line: <c>formula-name 1.2.3 [older-version …]</c>
    /// The first version token is the currently linked (active) version.
    /// </summary>
    private DiscoveredApp? ParseFormulaLine(string line, Dictionary<string, string> descriptions)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1)
        {
            return null;
        }

        var name = parts[0];
        var version = parts.Length >= 2 ? parts[1] : null;
        descriptions.TryGetValue(name, out var desc);

        return new DiscoveredApp(
            name,
            new AppIdentifier(Name, DisplayName, "Formula"),
            AppKind.Packages,
            version,
            SuggestedMethod: UpdateMethod.HomebrewFormula,
            SuggestedMethodDetail: name,
            Description: desc);
    }

    /// <summary>
    /// Cask line: <c>cask-name 1.2.3</c>  (version may be <c>latest</c> for rolling casks)
    /// </summary>
    private DiscoveredApp? ParseCaskLine(
        string line,
        Dictionary<string, string> descriptions,
        Dictionary<string, string> displayNames)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1)
        {
            return null;
        }

        var token = parts[0];
        var rawVer = parts.Length >= 2 ? parts[1] : null;
        // "latest" is a rolling pseudo-version; store null so checkers know it's unversioned.
        var version = string.Equals(rawVer, "latest", StringComparison.OrdinalIgnoreCase) ? null : rawVer;
        descriptions.TryGetValue(token, out var desc);
        displayNames.TryGetValue(token, out var displayName);

        return new DiscoveredApp(
            displayName ?? token,
            new AppIdentifier(Name, DisplayName, "Cask"),
            AppKind.App,
            version,
            SuggestedMethod: UpdateMethod.HomebrewCask,
            SuggestedMethodDetail: token,
            Description: desc);
    }

    /// <summary>
    /// Parses the <c>brew info --json=v2 --installed</c> output into description and display-name maps.
    /// Failures are silently ignored — descriptions are non-critical.
    /// </summary>
    private (Dictionary<string, string> Descriptions, Dictionary<string, string> DisplayNames) ParseBrewInfo(ProcessResult result)
    {
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return (descriptions, displayNames);
        }

        try
        {
            var info = JsonSerializer.Deserialize(result.StandardOutput, HomebrewJsonContext.Default.BrewInfoRoot);

            if (info?.Formulae is not null)
            {
                foreach (var f in info.Formulae)
                {
                    if (!string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.Desc))
                    {
                        descriptions[f.Name] = f.Desc;
                    }
                }
            }

            if (info?.Casks is not null)
            {
                foreach (var c in info.Casks)
                {
                    if (string.IsNullOrWhiteSpace(c.Token))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(c.Desc))
                    {
                        descriptions[c.Token] = c.Desc;
                    }

                    var displayName = c.Name?.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                    if (!string.IsNullOrWhiteSpace(displayName))
                    {
                        displayNames[c.Token] = displayName;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to parse 'brew info --json=v2 --installed' output");
        }

        return (descriptions, displayNames);
    }

    private static IEnumerable<string> Split(string output)
    {
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l));
    }
}