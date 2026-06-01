using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

using apps.Infrastructure;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Go;

/// <summary>
/// Discovers the installed Go toolchain via <c>go version</c>.
/// </summary>
public sealed class GoScanner(IProcessRunner runner, IHttpClientFactory httpClientFactory, ILogger<GoScanner> logger)
    : IScanner
{
    private string? _executablePath;

    public int Order => 5;

    public string Name => "Go";

    /// <inheritdoc/>
    public string DisplayName => "Go";

    public OS SupportedOS => OS.MacOS | OS.Windows;
    public AppKind Kind => AppKind.DevTool;

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

        await foreach (var tool in EnumerateTools(cancellationToken))
        {
            yield return tool;
        }
    }

    public async IAsyncEnumerable<(AppRecord App, bool Error)> CheckAsync(AppRecord[] apps, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? latestGoVersion = null;
        try
        {
            latestGoVersion = await FetchLatestGoSdkVersionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch latest Go SDK version");
        }

        var moduleApps = new List<AppRecord>();
        foreach (var record in apps)
        {
            if (record.App.Identifier.Qualifier == "Sdk")
            {
                if (latestGoVersion is not null)
                {
                    record.App.LatestVersion = latestGoVersion;
                }

                yield return (record, false);
            }
            else if (record.App.UpdateMethod == UpdateMethod.PackageRegistry && record.App.UpdateMethodDetail is not null)
            {
                moduleApps.Add(record);
            }
            else
            {
                yield return (record, false);
            }
        }

        if (moduleApps.Count > 0)
        {
            await foreach (var item in moduleApps.WhenAll<AppRecord, (AppRecord Record, bool Error)>(CheckModuleVersionAsync, cancellationToken: cancellationToken))
            {
                yield return item;
            }
        }
    }

    /// <summary>
    /// Checks the latest version of a Go module from the Go module proxy and writes the result to the channel.
    /// </summary>
    private async Task CheckModuleVersionAsync(
        AppRecord record,
        ChannelWriter<(AppRecord Record, bool Error)> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            var latest = await FetchModuleLatestAsync(record.App.UpdateMethodDetail!, cancellationToken).ConfigureAwait(false);
            if (latest is not null)
            {
                record.App.LatestVersion = latest;
            }

            await writer.WriteAsync((record, false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Go module proxy check failed for {Package}",
                record.App.UpdateMethodDetail);
            await writer.WriteAsync((record, true), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fetches <c>/@latest</c> for a module path, walking up the path on 404 to handle
    /// <c>cmd/</c> subpackage paths that are not themselves module roots.
    /// </summary>
    private async Task<string?> FetchModuleLatestAsync(string modulePath, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("goproxy");
        var segments = modulePath.Split('/');

        for (var len = segments.Length; len >= Math.Min(3, segments.Length); len--)
        {
            var candidate = string.Join('/', segments, 0, len);
            using var response = await client.GetAsync($"/{candidate}/@latest", cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var info = await JsonSerializer.DeserializeAsync(stream, GoJsonContext.Default.GoModuleLatest, cancellationToken).ConfigureAwait(false);
                return info?.Version?.TrimStart('v');
            }

            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// Fetches the latest stable Go release version from <c>https://go.dev/dl/?mode=json</c>.
    /// </summary>
    private async Task<string?> FetchLatestGoSdkVersionAsync(CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("go-dl");
        await using var stream = await client.GetStreamAsync("/dl/?mode=json", cancellationToken).ConfigureAwait(false);
        var releases = await JsonSerializer.DeserializeAsync(stream, GoJsonContext.Default.GoReleaseArray, cancellationToken).ConfigureAwait(false);

        if (releases is null or { Length: 0 })
        {
            return null;
        }

        var latest = releases.FirstOrDefault(r => r.Stable);
        // Version format: "go1.22.4" → strip "go" prefix
        return latest?.Version?.TrimStart('g', 'o');
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateSdk([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(_executablePath!, "version", cancellationToken);
        if (result.Success)
        {
            // "go version go1.22.4 darwin/arm64"
            var version = result.StandardOutput.Split(' ').First(part => part.StartsWith("go", StringComparison.Ordinal) && part.Length > 2 && char.IsDigit(part[2])).Split("go")[1];
            yield return new DiscoveredApp(this, "go",
                new AppIdentifier(Name, DisplayName, "Sdk"),
                AppKind.DevTool)
            {
                UpdateMethod = UpdateMethod.Sdk,
                Path = _executablePath!,
                InstalledVersion = version
            };
        }
        else
        {
            logger.LogWarning("'go version' failed: {Err}", result.StandardError.Trim());
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateTools([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Resolve GOPATH
        var gopathResult = await runner.RunAsync(_executablePath!, "env GOPATH", cancellationToken);
        if (gopathResult.Success && !string.IsNullOrWhiteSpace(gopathResult.StandardOutput))
        {
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

            foreach (var binaryPath in binaries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip non-executable files and hidden files
                var name = Path.GetFileName(binaryPath);
                if (name.StartsWith('.'))
                {
                    continue;
                }

                var result = await runner.RunAsync(_executablePath!, $"version -m \"{binaryPath}\"", cancellationToken);

                if (!result.Success
                    || result.StandardOutput.Contains("not an executable", StringComparison.OrdinalIgnoreCase)
                    || result.StandardOutput.Contains("not a Go executable", StringComparison.OrdinalIgnoreCase))
                {
                    // Binary is not a Go module binary — emit without version
                    yield return new DiscoveredApp(this, name,
                        new AppIdentifier(Name, DisplayName, "Tool"),
                        AppKind.DevTool)
                    {
                        Path = binaryPath,
                        InstalledVersion = null,
                        UpdateMethod = UpdateMethod.Specialised,
                        UpdateMethodDetail = binaryPath
                    };
                    continue;
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
                    : name;

                yield return new DiscoveredApp(this, displayName,
                    new AppIdentifier(Name, DisplayName, "Module"),
                    AppKind.DevTool)
                {
                    InstalledVersion = moduleVersion,
                    Path = binaryPath,
                    UpdateMethod = moduleVersion is not null ? UpdateMethod.PackageRegistry : null,
                    UpdateMethodDetail = modulePath
                };
            }
        }
        else
        {
            logger.LogWarning("'go env GOPATH' failed or returned empty");
            yield break;
        }
    }
}