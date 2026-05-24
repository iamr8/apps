using System.Runtime.CompilerServices;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Go;

/// <summary>
/// Discovers the installed Go toolchain via <c>go version</c>.
/// </summary>
public sealed class GoScanner(IProcessRunner runner, ILogger<GoScanner> logger)
    : IScanner
{
    public string Name => "Go";

    /// <inheritdoc/>
    public string DisplayName => "Go";

    /// <inheritdoc/>
    public string? GetSourceQualifier(AppKind kind) => "SDK";

    public bool IsAvailable()
    {
        return ScannerHelper.IsExecutableAvailable("go");
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var go = ScannerHelper.FindExecutable("go") ?? "go";
        var result = await runner.RunAsync(go, "version", cancellationToken);
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
            Name,
            AppKind.Packages,
            version,
            go,
            SuggestedMethod: UpdateMethod.Sdk);
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
}
