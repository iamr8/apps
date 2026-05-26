using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.VsCode;

/// <summary>
/// Discovers VS Code extensions via <c>code --list-extensions --show-versions</c>.
/// Each extension is emitted as <see cref="AppKind.Extension"/>;
/// the marketplace display name is resolved from the local <c>package.json</c>
/// and stored in <see cref="DiscoveredApp.Description"/> for two-line display.
/// </summary>
public sealed class VsCodeExtScanner(IProcessRunner runner, ILogger<VsCodeExtScanner> logger)
    : IScanner
{
    private string? _executablePath;

    public string Name => "VSCode";

    /// <inheritdoc/>
    public string DisplayName => "VS Code";

    public OS SupportedOS => OS.MacOS | OS.Windows;

    /// <inheritdoc/>
    public string? GetSourceQualifier(AppKind kind) => kind == AppKind.Extension ? "Extension" : null;

    private static readonly string ExtensionsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");

    /// <inheritdoc/>
    public bool IsAvailable()
    {
        _executablePath = OperatingSystem.IsWindows()
            ? ScannerHelper.FindExecutable("code.cmd")
            : ScannerHelper.FindExecutable("code");
        return _executablePath is not null;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(_executablePath!, "--list-extensions --show-versions", cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning("'code --list-extensions' failed: {Err}", result.StandardError.Trim());
            yield break;
        }

        var displayNames = BuildDisplayNameIndex();

        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var app = ParseLine(line, displayNames);
            if (app is not null)
            {
                yield return app;
            }
        }
    }

    /// <summary>
    /// Builds a dictionary from extension ID (lower-cased) to its marketplace display name
    /// by scanning the local <c>~/.vscode/extensions/</c> directory.
    /// </summary>
    private Dictionary<string, string> BuildDisplayNameIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(ExtensionsRoot))
        {
            return index;
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(ExtensionsRoot))
            {
                var folderName = Path.GetFileName(dir);

                // Skip hidden directories VS Code uses for staging/temp (e.g. .ebf6263f-…);
                // real extension folders always follow the {publisher}.{name}-{version} format.
                if (folderName.StartsWith('.'))
                {
                    continue;
                }

                var pkgPath = Path.Combine(dir, "package.json");
                if (!File.Exists(pkgPath))
                {
                    continue;
                }

                // Folder name format: {publisher}.{name}-{version}
                var hyphen = folderName.LastIndexOf('-');
                if (hyphen <= 0)
                {
                    continue;
                }

                var extensionId = folderName[..hyphen];
                if (string.IsNullOrWhiteSpace(extensionId) || index.ContainsKey(extensionId))
                {
                    continue;
                }

                try
                {
                    using var stream = File.OpenRead(pkgPath);
                    var pkg = JsonSerializer.Deserialize(stream, VsCodeJsonContext.Default.VsCodePackageJson);
                    var displayName = pkg?.DisplayName?.Trim();
                    if (!string.IsNullOrEmpty(displayName) && displayName != extensionId)
                    {
                        index[extensionId] = displayName;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not read package.json at {Path}", pkgPath);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Cannot enumerate VS Code extensions directory: {Root}", ExtensionsRoot);
        }

        return index;
    }

    /// <summary>
    /// Line format: <c>publisher.extension-id@1.2.3</c>
    /// </summary>
    private DiscoveredApp? ParseLine(string line, Dictionary<string, string> displayNames)
    {
        var atIdx = line.LastIndexOf('@');
        if (atIdx < 0)
        {
            return null;
        }

        var extensionId = line[..atIdx];
        var version = line[(atIdx + 1)..];

        if (string.IsNullOrWhiteSpace(extensionId))
        {
            return null;
        }

        displayNames.TryGetValue(extensionId, out var displayName);

        return new DiscoveredApp(
            extensionId,
            new AppIdentifier(Name, DisplayName, "Extension"),
            AppKind.Extension,
            string.IsNullOrWhiteSpace(version) ? null : version,
            SuggestedMethod: UpdateMethod.Specialised,
            SuggestedMethodDetail: extensionId,
            Description: displayName);
    }
}