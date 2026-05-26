using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Go;

/// <summary>
/// Discovers the installed Go toolchain via <c>go version</c>.
/// </summary>
public sealed class GoScanner(IProcessRunner runner, ProjectManifestFinder finder, ILogger<GoScanner> logger)
    : IScanner
{
    private string? _executablePath;

    public string Name => "Go";

    /// <inheritdoc/>
    public string DisplayName => "Go";

    public OS SupportedOS => OS.MacOS | OS.Windows;

    /// <inheritdoc/>
    public string? GetSourceQualifier(AppKind kind) => "SDK";

    public bool IsAvailable()
    {
        _executablePath = ScannerHelper.FindExecutable("go");
        return _executablePath is not null;
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var sdk in EnumerateSdk(cancellationToken))
        {
            yield return sdk;
        }

        await foreach (var module in EnumerateModules(cancellationToken))
        {
            yield return module;
        }

        await foreach (var tool in EnumerateTools(cancellationToken))
        {
            yield return tool;
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateSdk([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(_executablePath!, "version", cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning("'go version' failed: {Err}", result.StandardError.Trim());
            yield break;
        }

        // "go version go1.22.4 darwin/arm64"
        var version = ParseGoVersion(result.StandardOutput.Trim());
        if (version is null)
        {
            yield break;
        }

        yield return new DiscoveredApp(
            "go",
            new AppIdentifier(Name, DisplayName),
            AppKind.Packages,
            version,
            _executablePath!,
            SuggestedMethod: UpdateMethod.Sdk);
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateModules([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var manifestPath in finder.FindAsync("go.mod", cancellationToken))
        {
            await foreach (var app in ParseGoModAsync(manifestPath, cancellationToken))
            {
                yield return app;
            }
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateTools([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Resolve GOPATH
        var gopathResult = await runner.RunAsync(_executablePath!, "env GOPATH", cancellationToken);
        if (!gopathResult.Success || string.IsNullOrWhiteSpace(gopathResult.StandardOutput))
        {
            logger.LogWarning("'go env GOPATH' failed or returned empty");
            yield break;
        }

        var gopath = gopathResult.StandardOutput.Trim();
        var binDir = Path.Combine(gopath, "bin");

        if (!Directory.Exists(binDir))
        {
            logger.LogDebug("GOPATH/bin does not exist: {BinDir}", binDir);
            yield break;
        }

        string[] binaries;
        try
        {
            binaries = Directory.GetFiles(binDir, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cannot list GOPATH/bin: {BinDir}", binDir);
            yield break;
        }

        foreach (var binary in binaries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip non-executable files and hidden files
            var name = Path.GetFileName(binary);
            if (name.StartsWith('.'))
            {
                continue;
            }

            var app = await BuildEntryAsync(_executablePath!, binary, name, cancellationToken);
            if (app is not null)
            {
                yield return app;
            }
        }
    }

    private static string? ParseGoVersion(string output)
    {
        // Find "go1.22.4" token and strip the leading "go"
        foreach (var token in output.Split(' '))
        {
            if (token.StartsWith("go", StringComparison.Ordinal) && token.Length > 2 && char.IsDigit(token[2]))
            {
                return token[2..]; // "1.22.4"
            }
        }

        return null;
    }

    private async IAsyncEnumerable<DiscoveredApp> ParseGoModAsync(string manifestPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(manifestPath, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Cannot read {Path}", manifestPath);
            yield break;
        }

        var inRequireBlock = false;

        foreach (var rawLine in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = rawLine.Trim();

            // Strip inline comments
            var commentIdx = line.IndexOf("//", StringComparison.Ordinal);
            if (commentIdx >= 0) line = line[..commentIdx].Trim();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line == "require (")
            {
                inRequireBlock = true;
                continue;
            }

            if (line == ")")
            {
                inRequireBlock = false;
                continue;
            }

            // Inline require: "require module/path v1.2.3"
            if (line.StartsWith("require ", StringComparison.Ordinal) && !line.EndsWith("("))
            {
                var app = ParseRequireLine(line["require ".Length..].Trim(), manifestPath);
                if (app is not null) yield return app;
                continue;
            }

            if (inRequireBlock)
            {
                var app = ParseRequireLine(line, manifestPath);
                if (app is not null) yield return app;
            }
        }
    }

    /// <summary>
    /// Parses "module/path v1.2.3" or "module/path v1.2.3 // indirect"
    /// </summary>
    private DiscoveredApp? ParseRequireLine(string line, string manifestPath)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var modulePath = parts[0];
        var version = parts[1].TrimStart('v');

        // Display name: last path component of the module path
        var displayName = modulePath.TrimEnd('/');
        var slashIdx = displayName.LastIndexOf('/');
        if (slashIdx >= 0) displayName = displayName[(slashIdx + 1)..];

        return new DiscoveredApp(
            displayName,
            new AppIdentifier(Name, DisplayName, "Module"),
            AppKind.Libraries,
            version,
            ProjectFile: manifestPath,
            SuggestedMethod: UpdateMethod.PackageRegistry,
            SuggestedMethodDetail: modulePath);
    }

    private async Task<DiscoveredApp?> BuildEntryAsync(string go, string binaryPath, string binaryName, CancellationToken ct)
    {
        var result = await runner.RunAsync(go, $"version -m \"{binaryPath}\"", ct);

        if (!result.Success
            || result.StandardOutput.Contains("not an executable", StringComparison.OrdinalIgnoreCase)
            || result.StandardOutput.Contains("not a Go executable", StringComparison.OrdinalIgnoreCase))
        {
            // Binary is not a Go module binary — emit without version
            return new DiscoveredApp(
                binaryName,
                new AppIdentifier(Name, DisplayName, "Tool"),
                AppKind.Packages,
                null,
                binaryPath);
        }

        // Parse the output lines:
        //   /path/to/binary: go1.22.3
        //   \tpath\tmodule/path
        //   \tmod\tmodule/path\tv1.2.3\th1:...
        string? modulePath = null;
        string? moduleVersion = null;

        foreach (var line in result.StandardOutput.Split('\n'))
        {
            var trimmed = line.TrimStart('\t');
            if (trimmed.StartsWith("path\t", StringComparison.Ordinal))
            {
                modulePath = trimmed["path\t".Length..].Trim();
            }
            else if (trimmed.StartsWith("mod\t", StringComparison.Ordinal))
            {
                var parts = trimmed.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                // parts: ["mod", "module/path", "v1.2.3", "h1:..."]
                if (parts.Length >= 3)
                {
                    moduleVersion = parts[2].TrimStart('v');
                }
            }
        }

        var displayName = modulePath is not null
            ? Path.GetFileName(modulePath.TrimEnd('/'))
            : binaryName;

        return new DiscoveredApp(
            displayName,
            new AppIdentifier(Name, DisplayName, "Module"),
            AppKind.Packages,
            moduleVersion,
            binaryPath,
            SuggestedMethod: moduleVersion is not null ? UpdateMethod.PackageRegistry : null,
            SuggestedMethodDetail: modulePath);
    }
}