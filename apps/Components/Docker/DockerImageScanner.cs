using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

namespace apps.Components.Docker;

/// <summary>
/// Discovers local Docker images via <c>docker images</c> and checks each image's tag
/// against Docker Hub to detect content changes behind a stable tag.
/// One <see cref="AppKind.DevTool"/> entry is emitted per unique repository:tag pair.
/// Images with no registry digest (locally built) or from private registries are skipped.
/// </summary>
public sealed class DockerImageScanner(IProcessRunner runner, IHttpClientFactory httpClientFactory, ILogger<DockerImageScanner> logger)
    : IScanner
{
    private static readonly string PreferredArch =
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "amd64";

    private string? _executablePath;

    public string Name => "Docker";

    /// <inheritdoc/>
    public string DisplayName => "Docker";

    public OS SupportedOS => OS.MacOS | OS.Windows;
    public AppKind Kind => AppKind.DevTool;

    public bool IsAvailable()
    {
        _executablePath = ScannerHelper.FindExecutable("docker");
        return _executablePath is not null;
    }

    /// <inheritdoc/>
    public bool StripTagFromDisplayName => true;

    /// <inheritdoc/>
    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        const string format = @"--format {{.Repository}}|{{.Tag}}|{{.Digest}}|{{.ID}}";
        var result = await runner.RunAsync(_executablePath!, $"images {format}", cancellationToken);

        if (!result.Success)
        {
            logger.LogWarning("'docker images' failed ({Code}): {Err}", result.ExitCode, result.StandardError.Trim());
            yield break;
        }

        var localImageIds = await GetLocallyBuiltImageIdsAsync(cancellationToken);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (ParseLine(line, seen, localImageIds) is { } app)
            {
                yield return app;
            }
        }
    }

    /// <summary>
    /// Returns the IDs of images built locally by Docker Compose (identified by the
    /// <c>com.docker.compose.project</c> label). Such images are not published to any registry,
    /// so checking them against Docker Hub only produces spurious errors.
    /// </summary>
    private async Task<HashSet<string>> GetLocallyBuiltImageIdsAsync(CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = await runner.RunAsync(_executablePath!, "images --filter label=com.docker.compose.project --format {{.ID}}", cancellationToken);

        if (!result.Success)
        {
            return ids;
        }

        foreach (var id in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ids.Add(id);
        }

        return ids;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<(AppRecord App, bool Error)> CheckAsync(AppRecord[] apps, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (apps.Length == 0)
        {
            yield break;
        }

        await foreach (var item in apps.WhenAll<AppRecord, (AppRecord Record, bool Error)>(CheckImageAsync, cancellationToken: cancellationToken))
        {
            yield return item;
        }
    }

    /// <summary>
    /// Compares the locally stored sha256 digest against the remote digest from Docker Hub.
    /// </summary>
    private async Task CheckImageAsync(
        AppRecord record,
        ChannelWriter<(AppRecord Record, bool Error)> writer,
        CancellationToken cancellationToken)
    {
        var imageRef = record.App.UpdateInfo;
        if (string.IsNullOrWhiteSpace(imageRef))
        {
            await writer.WriteAsync((record, false), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryParseImageRef(imageRef, out var ns, out var repo, out var tag))
        {
            // Private-registry reference — we only query Docker Hub, so its status is unknown.
            record.CheckFailed = true;
            await writer.WriteAsync((record, false), cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            using var client = httpClientFactory.CreateClient("dockerhub");
            using var response = await client
                .GetAsync($"/v2/repositories/{ns}/{repo}/tags/{tag}", cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode is System.Net.HttpStatusCode.NotFound)
            {
                // Tag/repo not on Docker Hub (e.g. a locally-built image) — unresolvable, not a failure.
                logger.LogDebug("Docker Hub has no tag {Image}; treating as unresolvable", imageRef);
                record.CheckFailed = true;
                await writer.WriteAsync((record, false), cancellationToken).ConfigureAwait(false);
                return;
            }

            response.EnsureSuccessStatusCode();

            var tagInfo = await response.Content
                .ReadFromJsonAsync(DockerJsonContext.Default.DockerTagInfo, cancellationToken)
                .ConfigureAwait(false);

            var remoteDigest = tagInfo?.Digest;

            if (string.IsNullOrWhiteSpace(remoteDigest))
            {
                remoteDigest = tagInfo?.Images?
                    .FirstOrDefault(img =>
                        string.Equals(img.Os, "linux", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(img.Architecture, PreferredArch, StringComparison.OrdinalIgnoreCase))
                    ?.Digest;
            }

            if (string.IsNullOrWhiteSpace(remoteDigest))
            {
                remoteDigest = tagInfo?.Images?
                    .FirstOrDefault(img => string.Equals(img.Os, "linux", StringComparison.OrdinalIgnoreCase))
                    ?.Digest;
            }

            if (string.IsNullOrWhiteSpace(remoteDigest))
            {
                await writer.WriteAsync((record, false), cancellationToken).ConfigureAwait(false);
                return;
            }

            var localDigest = record.App.Digest;
            if (!string.IsNullOrWhiteSpace(localDigest)
                && localDigest.StartsWith("sha256:", StringComparison.Ordinal)
                && !string.Equals(localDigest, remoteDigest, StringComparison.Ordinal))
            {
                record.App.LatestVersion = remoteDigest;
            }

            await writer.WriteAsync((record, false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch digest update for {Image}", imageRef);
            record.CheckFailed = true;
            await writer.WriteAsync((record, true), cancellationToken).ConfigureAwait(false);
        }
    }

    private DiscoveredApp? ParseLine(string line, HashSet<string> seen, HashSet<string> localImageIds)
    {
        var parts = line.Split('|', 4);
        if (parts.Length < 4)
        {
            return null;
        }

        var repo = parts[0].Trim();
        var tag = parts[1].Trim();

        if (repo == "<none>" || tag == "<none>")
        {
            return null;
        }

        // Skip images built locally by Compose that carry no registry host — they exist only on
        // this machine, so a Docker Hub lookup would always fail. Host-qualified references
        // (e.g. mcr.microsoft.com/...) stay, even if Compose-labelled.
        var id = parts[3].Trim();
        if (localImageIds.Contains(id) && !HasRegistryHost(repo))
        {
            return null;
        }

        var imageRef = $"{repo}:{tag}";
        if (!seen.Add(imageRef))
        {
            return null;
        }

        var digest = parts[2].Trim() is { Length: > 0 } d && d != "<none>" ? d : null;

        if (digest is null)
        {
            return null;
        }

        return new DiscoveredApp(this,
            imageRef,
            new AppIdentifier(Name, DisplayName, "Image"),
            AppKind.DevTool)
        {
            InstalledVersion = tag,
            Digest = digest,
            Attribute = AppAttribute.DevTool | AppAttribute.Image,
            UpdateInfo = imageRef,
        };
    }

    /// <summary>
    /// Splits an image reference into its Docker Hub namespace, repository, and tag.
    /// Official images get the <c>library</c> namespace and a missing tag defaults to <c>latest</c>.
    /// Returns <see langword="false"/> for private-registry references (host contains a '.' or ':').
    /// </summary>
    /// <summary>
    /// Returns <see langword="true"/> when a repository name is qualified by a registry host
    /// (its first path segment contains a '.' or ':' — e.g. <c>mcr.microsoft.com/...</c>).
    /// </summary>
    internal static bool HasRegistryHost(string repo)
    {
        var slashIdx = repo.IndexOf('/');
        if (slashIdx < 0)
        {
            return false;
        }

        var host = repo[..slashIdx];
        return host.Contains('.') || host.Contains(':');
    }

    internal static bool TryParseImageRef(string imageRef, out string ns, out string repo, out string tag)
    {
        ns = repo = tag = string.Empty;

        var colonIdx = imageRef.LastIndexOf(':');
        var imageNoTag = colonIdx > 0 ? imageRef[..colonIdx] : imageRef;
        tag = colonIdx > 0 ? imageRef[(colonIdx + 1)..] : "latest";

        var slashIdx = imageNoTag.IndexOf('/');

        if (slashIdx < 0)
        {
            ns = "library";
            repo = imageNoTag;
            return !string.IsNullOrWhiteSpace(repo);
        }

        var possibleHost = imageNoTag[..slashIdx];

        if (possibleHost.Contains('.') || possibleHost.Contains(':'))
        {
            return false;
        }

        ns = possibleHost;
        repo = imageNoTag[(slashIdx + 1)..];
        return !string.IsNullOrWhiteSpace(ns) && !string.IsNullOrWhiteSpace(repo);
    }
}

internal sealed class DockerTagInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Manifest list (multi-arch index) digest — matches what <c>docker images</c> reports locally.
    /// </summary>
    [JsonPropertyName("digest")]
    public string? Digest { get; init; }

    [JsonPropertyName("images")]
    public DockerImageInfo[]? Images { get; init; }
}

internal sealed class DockerImageInfo
{
    [JsonPropertyName("architecture")]
    public string? Architecture { get; init; }

    [JsonPropertyName("os")]
    public string? Os { get; init; }

    [JsonPropertyName("digest")]
    public string? Digest { get; init; }
}

[JsonSerializable(typeof(DockerTagInfo))]
internal sealed partial class DockerJsonContext : JsonSerializerContext;