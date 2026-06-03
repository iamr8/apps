using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

namespace apps.Components.Dotnet;

/// <summary>
/// Discovers .NET SDKs, runtimes, and global tools installed on the system.
/// Checks SDKs/runtimes against the .NET releases index and global tools against the NuGet registry.
/// </summary>
public sealed class DotnetScanner(IHttpClientFactory httpClientFactory, IProcessRunner runner, ILogger<DotnetScanner> logger)
    : IScanner
{
    private readonly ConcurrentDictionary<string, Task<string?>> _inflightNuget = new(StringComparer.OrdinalIgnoreCase);

    private string? _executablePath;

    public string Name => "Dotnet";

    /// <inheritdoc/>
    public string DisplayName => ".NET";

    public OS SupportedOS => OS.MacOS | OS.Windows;
    public AppKind Kind => AppKind.DevTool;

    public bool IsAvailable()
    {
        _executablePath = ScannerHelper.FindExecutable("dotnet");
        return _executablePath is not null;
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var sdk in EnumerateSdks(cancellationToken))
        {
            yield return sdk;
        }

        await foreach (var runtime in EnumerateRuntimes(cancellationToken))
        {
            yield return runtime;
        }

        await foreach (var tool in EnumerateGlobalTools(cancellationToken))
        {
            yield return tool;
        }
    }

    public async IAsyncEnumerable<(AppRecord App, bool Error)> CheckAsync(AppRecord[] apps, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        DotnetReleasesIndex? releasesIndex = null;
        try
        {
            using var client = httpClientFactory.CreateClient("dotnet-releases");
            await using var stream = await client.GetStreamAsync("/dotnet/release-metadata/releases-index.json", cancellationToken).ConfigureAwait(false);
            releasesIndex = await JsonSerializer.DeserializeAsync(stream, DotnetJsonContext.Default.DotnetReleasesIndex, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch .NET releases index");
        }

        var nugets = new List<AppRecord>();
        foreach (var record in apps)
        {
            if (record.App.Identifier.Qualifier == "Global Tool")
            {
                nugets.Add(record);
            }
            else
            {
                if (releasesIndex?.ReleasesIndex is null || record.App.InstalledVersion is null)
                {
                    yield return (record, false);
                    continue;
                }

                var channelVersion = MajorMinor(record.App.InstalledVersion);
                var channel = releasesIndex.ReleasesIndex.FirstOrDefault(c => c.ChannelVersion.Equals(channelVersion, StringComparison.OrdinalIgnoreCase));
                if (channel is null)
                {
                    logger.LogDebug("No .NET releases channel found for {Version}", channelVersion);
                    yield return (record, false);
                    continue;
                }

                record.App.LatestVersion = record.App.Identifier.Qualifier == "Runtime" ? channel.LatestRuntime : channel.LatestSdk;
                yield return (record, false);
            }
        }

        if (nugets.Count > 0)
        {
            await foreach (var item in nugets.WhenAll<AppRecord, (AppRecord Record, bool Error)>(CheckNuGetVersionAsync, cancellationToken: cancellationToken))
            {
                yield return item;
            }
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateRuntimes([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(_executablePath!, "--list-runtimes", cancellationToken);
        if (result.Success)
        {
            var lines = result.StandardOutput.Trim()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c =>
                {
                    var parts = c.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                    var name = parts[0];
                    var version = parts[1];
                    var path = parts[2].Trim('[', ']');
                    return new
                    {
                        Name = $"{name} {MajorMinor(version)}",
                        Version = version,
                        Path = path
                    };
                })
                .GroupBy(l => l.Name) // Group by runtime name (e.g. "Microsoft.AspNetCore.App") to avoid duplicates when multiple versions are installed
                .Select(g => g.OrderByDescending(c => c.Version, VersionComparer.Instance).First()) // Take the latest version in each group
                .ToArray();
            foreach (var line in lines)
            {
                yield return new DiscoveredApp(this, line.Name,
                    new AppIdentifier(Name, DisplayName, "Runtime"),
                    AppKind.DevTool)
                {
                    InstalledVersion = line.Version,
                    Path = Path.Combine(line.Path, line.Version),
                    Attribute = AppAttribute.DevTool | AppAttribute.Sdk,
                };
            }
        }
        else
        {
            logger.LogWarning("'dotnet --list-runtimes' failed: {Err}", result.StandardError.Trim());
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateSdks([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Output: "8.0.300 [/usr/local/share/dotnet/sdk]"
        var result = await runner.RunAsync(_executablePath!, "--list-sdks", cancellationToken);
        if (result.Success)
        {
            var lines = result.StandardOutput.Trim()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c =>
                {
                    var parts = c.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    var version = parts[0];
                    var path = parts[1].Trim('[', ']');
                    return new
                    {
                        Name = MajorMinor(version),
                        Version = version,
                        Path = path
                    };
                })
                .GroupBy(l => l.Name)
                .Select(g => g.OrderByDescending(c => c.Version, VersionComparer.Instance).First()) // Take the latest version in each group
                .ToArray();
            foreach (var line in lines)
            {
                yield return new DiscoveredApp(this, $".NET {line.Name}",
                    new AppIdentifier(Name, DisplayName, "Sdk"),
                    AppKind.DevTool)
                {
                    InstalledVersion = line.Version,
                    Path = Path.Combine(line.Path, line.Version),
                    Attribute = AppAttribute.DevTool | AppAttribute.Sdk,
                };
            }
        }
        else
        {
            logger.LogWarning("'dotnet --list-sdks' failed: {Err}", result.StandardError.Trim());
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateGlobalTools([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(_executablePath!, "tool list -g", cancellationToken);
        if (result.Success)
        {
            var lines = result.StandardOutput.Trim()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Skip(2) // Skip header line
                .Select(c =>
                {
                    var parts = c.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                    var name = parts[0];
                    var version = parts[1];
                    var command = parts[2];
                    return new
                    {
                        Name = name,
                        Version = version,
                        Command = command
                    };
                })
                .ToArray();
            foreach (var line in lines)
            {
                yield return new DiscoveredApp(this, line.Name,
                    new AppIdentifier(Name, DisplayName, "Global Tool"),
                    AppKind.DevTool)
                {
                    InstalledVersion = line.Version,
                    Attribute = AppAttribute.DevTool,
                };
            }
        }
        else
        {
            logger.LogWarning("'dotnet tool list -g' failed: {Err}", result.StandardError.Trim());
        }
    }

    /// <summary>
    /// Checks the latest version of a global tool from the NuGet registry and writes the result to the channel.
    /// </summary>
    private async Task CheckNuGetVersionAsync(AppRecord record, ChannelWriter<(AppRecord Record, bool Error)> writer, CancellationToken cancellationToken)
    {
        try
        {
            var latest = await FetchLatestNuGetVersionAsync(record.App.Name, cancellationToken).ConfigureAwait(false);
            if (latest is not null)
            {
                record.App.LatestVersion = latest;
            }

            await writer.WriteAsync((record, false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "NuGet version check failed for {Package}", record.App.Name);
            await writer.WriteAsync((record, true), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fetches the latest stable version of a NuGet package from the flat container API.
    /// Uses deduplication to avoid redundant requests for the same package.
    /// </summary>
    private Task<string?> FetchLatestNuGetVersionAsync(string name, CancellationToken cancellationToken)
    {
        return _inflightNuget.GetOrAdd(name, id => FetchNuGetVersionCoreAsync(id, cancellationToken));
    }

    private async Task<string?> FetchNuGetVersionCoreAsync(string name, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("nuget");
        var lowerId = name.ToLowerInvariant();
        var url = $"/v3-flatcontainer/{lowerId}/index.json";

        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogDebug("NuGet returned {Status} for {Package}",
                response.StatusCode,
                name);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var index = await JsonSerializer.DeserializeAsync(stream, DotnetJsonContext.Default.NugetVersionIndex, cancellationToken).ConfigureAwait(false);

        if (index?.Versions is null or { Length: 0 })
        {
            return null;
        }

        // Return the last stable version (no prerelease tag)
        for (var i = index.Versions.Length - 1; i >= 0; i--)
        {
            var v = index.Versions[i];
            if (!v.Contains('-', StringComparison.Ordinal))
            {
                return v;
            }
        }

        return index.Versions[^1];
    }

    /// <summary>
    /// Returns the <c>major.minor</c> segment of a version string so that different
    /// installed SDK generations (e.g. 6.0 and 10.0) get unique names and are not
    /// collapsed by the name-based deduplication in <c>--show-all</c>.
    /// </summary>
    private static string MajorMinor(string version)
    {
        var firstDot = version.IndexOf('.');
        if (firstDot < 0)
        {
            return version;
        }

        var secondDot = version.IndexOf('.', firstDot + 1);
        return secondDot > 0 ? version[..secondDot] : version;
    }
}