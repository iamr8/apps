using System.Runtime.CompilerServices;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Go;

/// <summary>
/// Discovers Go binaries installed in GOPATH/bin and resolves their module versions
/// via <c>go version -m &lt;binary&gt;</c>.
/// Each binary is emitted as <see cref="AppKind.Package"/>.
/// </summary>
public sealed class GoToolsScanner(IProcessRunner runner, ILogger<GoToolsScanner> logger)
    : IScanner
{
    public string Name => "GoTools";

    /// <inheritdoc/>
    public string DisplayName => "Go";

    /// <inheritdoc/>
    public string? GetSourceQualifier(AppKind kind) => "Tool";

    public bool IsAvailable()
    {
        return ScannerHelper.IsExecutableAvailable("go");
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var go = ScannerHelper.FindExecutable("go") ?? "go";

        // Resolve GOPATH
        var gopathResult = await runner.RunAsync(go, "env GOPATH", cancellationToken);
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

            var app = await BuildEntryAsync(go, binary, name, cancellationToken);
            if (app is not null)
            {
                yield return app;
            }
        }
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
                Name,
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
                if (parts.Length >= 3) moduleVersion = parts[2].TrimStart('v');
            }
        }

        var displayName = modulePath is not null
            ? Path.GetFileName(modulePath.TrimEnd('/'))
            : binaryName;

        return new DiscoveredApp(
            displayName,
            Name,
            AppKind.Packages,
            moduleVersion,
            binaryPath,
            SuggestedMethod: moduleVersion is not null ? UpdateMethod.PackageRegistry : null,
            SuggestedMethodDetail: modulePath);
    }
}
