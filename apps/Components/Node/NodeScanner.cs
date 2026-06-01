using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

using apps.Infrastructure;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Node;

/// <summary>
/// Discovers installed Node.js versions and globally installed npm packages.
/// Node versions: reads <c>node --version</c> (system) or lists directories under
/// <c>~/.nvm/versions/node/</c> (nvm). Global npm packages: parses
/// <c>npm list -g --depth=0 --json</c>.
/// Update checks query <c>https://registry.npmjs.org/{name}/latest</c>.
/// </summary>
public sealed class NodeScanner(IProcessRunner runner, IHttpClientFactory httpClientFactory, ILogger<NodeScanner> logger)
    : IScanner
{
    private const string ExecutableName = "node";

    private readonly ConcurrentDictionary<string, Task<string?>> _inflightNpm = new(StringComparer.OrdinalIgnoreCase);

    private string? _nodeExecutablePath;
    private string? _npmExecutablePath;
    private string? _nvmVersionsPath;

    public int Order => 5;

    public string Name => "Node";

    /// <inheritdoc/>
    public string DisplayName => "Node";

    public OS SupportedOS => OS.MacOS | OS.Windows;
    public AppKind Kind => AppKind.DevTool | AppKind.Package;

    public bool IsAvailable()
    {
        _nodeExecutablePath = ScannerHelper.FindExecutable(ExecutableName);
        _npmExecutablePath = ScannerHelper.FindExecutable("npm");

        var nvmPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nvm", "versions", "node");
        if (Directory.Exists(nvmPath))
        {
            _nvmVersionsPath = nvmPath;
        }

        return _nodeExecutablePath is not null || _nvmVersionsPath is not null;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var app in EnumerateNodeVersions(cancellationToken))
        {
            yield return app;
        }

        await foreach (var app in EnumerateGlobalPackages(cancellationToken))
        {
            yield return app;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<(AppRecord App, bool Error)> CheckAsync(AppRecord[] apps, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (apps.Length == 0)
        {
            yield break;
        }

        var sdkApps = new List<AppRecord>();
        var npmApps = new List<AppRecord>();

        foreach (var record in apps)
        {
            if (record.App.UpdateMethod == UpdateMethod.Sdk)
            {
                sdkApps.Add(record);
            }
            else
            {
                npmApps.Add(record);
            }
        }

        foreach (var record in sdkApps)
        {
            yield return (record, false);
        }

        if (npmApps.Count > 0)
        {
            await foreach (var item in npmApps.WhenAll<AppRecord, (AppRecord Record, bool Error)>(CheckNpmVersionAsync, cancellationToken: cancellationToken))
            {
                yield return item;
            }
        }
    }

    /// <summary>
    /// Queries the npm registry for the latest version of a single package.
    /// Uses in-flight deduplication to avoid redundant requests.
    /// </summary>
    private async Task CheckNpmVersionAsync(AppRecord record, ChannelWriter<(AppRecord Record, bool Error)> writer, CancellationToken cancellationToken)
    {
        var packageName = record.App.UpdateMethodDetail ?? record.App.PackageId ?? record.App.Name;

        try
        {
            var latest = await _inflightNpm
                .GetOrAdd(packageName, name => FetchLatestNpmVersionAsync(name, cancellationToken))
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(latest))
            {
                record.App.LatestVersion = latest;
            }

            await writer.WriteAsync((record, false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "npm registry check failed for {Package}",
                packageName);
            await writer.WriteAsync((record, true), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fetches the latest version of an npm package from the registry.
    /// Handles scoped packages by encoding the slash.
    /// </summary>
    private async Task<string?> FetchLatestNpmVersionAsync(string packageName, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("npm");
        var path = BuildRegistryPath(packageName);

        using var response = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogDebug("npm registry returned {Status} for {Package}",
                response.StatusCode,
                packageName);
            return null;
        }

        var info = await response.Content
            .ReadFromJsonAsync(NodeJsonContext.Default.NpmPackageLatest, cancellationToken)
            .ConfigureAwait(false);

        return info?.Version;
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateNodeVersions([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_nvmVersionsPath is not null)
        {
            foreach (var app in ScanNvm())
            {
                yield return app;
            }
        }
        else if (_nodeExecutablePath is not null)
        {
            var app = await ScanSystemNodeAsync(cancellationToken);
            if (app is not null)
            {
                yield return app;
            }
        }
    }

    private IEnumerable<DiscoveredApp> ScanNvm()
    {
        string[] versions;
        try
        {
            versions = Directory.GetDirectories(_nvmVersionsPath!, "v*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cannot list nvm versions in {Dir}", _nvmVersionsPath);
            yield break;
        }

        foreach (var dir in versions)
        {
            var versionTag = Path.GetFileName(dir);
            var version = versionTag.TrimStart('v');

            yield return new DiscoveredApp(this,
                ExecutableName,
                new AppIdentifier(Name, DisplayName, "Sdk"),
                AppKind.DevTool)
            {
                InstalledVersion = version,
                Path = dir,
                UpdateMethod = UpdateMethod.Sdk,
            };
        }
    }

    private async Task<DiscoveredApp?> ScanSystemNodeAsync(CancellationToken ct)
    {
        var result = await runner.RunAsync(_nodeExecutablePath!, "--version", ct);
        if (!result.Success)
        {
            return null;
        }

        var version = result.StandardOutput.Trim().TrimStart('v');
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return new DiscoveredApp(this,
            ExecutableName,
            new AppIdentifier(Name, DisplayName, "Sdk"),
            AppKind.DevTool)
        {
            InstalledVersion = version,
            Path = _nodeExecutablePath!,
            UpdateMethod = UpdateMethod.Sdk,
        };
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateGlobalPackages([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_npmExecutablePath is null)
        {
            yield break;
        }

        var result = await runner.RunAsync(_npmExecutablePath!, "list -g --depth=0 --json", cancellationToken);

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            logger.LogWarning("'npm list -g' produced no output. Err: {Err}", result.StandardError.Trim());
            yield break;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(result.StandardOutput);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse 'npm list -g' JSON output");
            yield break;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("dependencies", out var deps))
            {
                yield break;
            }

            foreach (var entry in deps.EnumerateObject())
            {
                var packageName = entry.Name;
                string? version = null;

                if (entry.Value.TryGetProperty("version", out var verProp))
                {
                    version = verProp.GetString();
                }

                yield return new DiscoveredApp(this,
                    packageName,
                    new AppIdentifier(Name, "npm", "Global Package"),
                    AppKind.DevTool)
                {
                    PackageId = packageName,
                    InstalledVersion = version,
                    UpdateMethod = UpdateMethod.PackageRegistry,
                    UpdateMethodDetail = packageName,
                };
            }
        }
    }

    /// <summary>
    /// Builds the registry path for a package name, URL-encoding the '/' in scoped
    /// packages so that <c>@scope/name</c> becomes <c>/@scope%2Fname/latest</c>.
    /// </summary>
    private static string BuildRegistryPath(string packageName)
    {
        if (!packageName.StartsWith('@'))
        {
            return $"/{packageName}/latest";
        }

        var slashIdx = packageName.IndexOf('/', 1);
        if (slashIdx < 0)
        {
            return $"/{packageName}/latest";
        }

        var scope = packageName[..slashIdx];
        var name = packageName[(slashIdx + 1)..];
        return $"/{scope}%2F{name}/latest";
    }
}

internal sealed class NpmPackageLatest
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

[JsonSerializable(typeof(NpmPackageLatest))]
internal sealed partial class NodeJsonContext : JsonSerializerContext;