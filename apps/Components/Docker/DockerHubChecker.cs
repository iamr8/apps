using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

using apps.Models;
using apps.Checkers;

using Microsoft.Extensions.Logging;

namespace apps.Components.Docker;

/// <summary>
/// Specialised checker — compares the locally stored sha256 digest against the digest
/// of the same tag fetched from Docker Hub's tag API.
///
/// Handles apps from the <c>Docker</c> scanner (<see cref="AppRecord.UpdateMethodDetail"/>
/// holds the full image reference, e.g. <c>python:3.12</c>;
/// <see cref="AppRecord.InstalledVersion"/> holds the tag, e.g. <c>3.12</c>;
/// <see cref="AppRecord.Digest"/> holds the local sha256 used for comparison).
/// Official library images (e.g. <c>python</c> without a namespace) are resolved under
/// the <c>library</c> namespace.
///
/// When up to date the result surfaces the tag in both version fields.
/// When outdated the installed field shows the tag and the latest field shows the new sha256,
/// e.g. <c>3.12 → sha256:abc…</c>.
/// Private-registry images (those whose repository contains a hostname dot or colon) are skipped.
/// All checks fan out concurrently; results stream as each HTTP response arrives.
/// </summary>
public sealed class DockerHubChecker(IHttpClientFactory httpClientFactory, ILogger<DockerHubChecker> logger)
    : IUpdateChecker
{
    // linux/amd64 on Intel Mac; linux/arm64 on Apple Silicon
    private static readonly string PreferredArch =
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "amd64";

    /// <inheritdoc/>
    public UpdateMethod Method => UpdateMethod.Specialised;

    /// <inheritdoc/>
    public string DisplayName => "Docker Hub";

    /// <inheritdoc/>
    public bool CanCheck(AppRecord app)
        => app.UpdateMethod == UpdateMethod.Specialised
           && string.Equals(app.Identifier.Name, "Docker", StringComparison.Ordinal)
           && app.UpdateMethodDetail is not null;

    /// <inheritdoc/>
    public async Task<UpdateCheckResult> CheckAsync(AppRecord app, CancellationToken cancellationToken = default)
        => await CheckOneAsync(app, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<UpdateCheckResult>> CheckBatchAsync(
        IReadOnlyList<AppRecord> apps,
        CancellationToken cancellationToken = default)
    {
        var tasks = apps.Select(a => CheckOneAsync(a, cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<UpdateCheckResult> CheckStreamAsync(
        IReadOnlyList<AppRecord> apps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var task in Task.WhenEach(apps.Select(a => CheckOneAsync(a, cancellationToken))).ConfigureAwait(false))
        {
            yield return await task.ConfigureAwait(false);
        }
    }

    private async Task<UpdateCheckResult> CheckOneAsync(AppRecord app, CancellationToken cancellationToken)
    {
        var imageRef = app.UpdateMethodDetail!;

        if (!TryParseImageRef(imageRef, out var ns, out var repo, out var tag))
        {
            // Private registry or unresolvable format — skip silently
            return new UpdateCheckResult(app.Name, UpdateMethod.Specialised, false, app.InstalledVersion, app.InstalledVersion);
        }

        try
        {
            using var client = httpClientFactory.CreateClient("dockerhub");
            var tagInfo = await client
                .GetFromJsonAsync(
                    $"/v2/repositories/{ns}/{repo}/tags/{tag}",
                    DockerHubJsonContext.Default.DockerTagInfo,
                    cancellationToken)
                .ConfigureAwait(false);

            // Prefer the manifest-list digest — this is what `docker images` reports locally.
            // Fall back to per-architecture image digest only when the tag-level digest is absent.
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
                return Err(app, "Docker Hub returned no image digest for this tag");
            }

            var localDigest = app.Digest;

            if (string.IsNullOrWhiteSpace(localDigest) || !localDigest.StartsWith("sha256:", StringComparison.Ordinal))
            {
                return new UpdateCheckResult(app.Name, UpdateMethod.Specialised, false, app.InstalledVersion, app.InstalledVersion);
            }

            var installedTag = app.InstalledVersion;
            var updateAvailable = !string.Equals(localDigest, remoteDigest, StringComparison.Ordinal);

            // Up to date: show tag → tag. Outdated: show tag → sha256:newdigest.
            return new UpdateCheckResult(app.Name, UpdateMethod.Specialised, updateAvailable, installedTag, updateAvailable ? remoteDigest : installedTag);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "Docker Hub check failed for {Imsage}: {Message}",
                imageRef,
                ex.Message);
            logger.LogDebug(ex,
                "Docker Hub check exception detail for {Image}",
                imageRef);
            return Err(app, ex.Message);
        }
    }

    /// <summary>
    /// Splits an image reference into its Hub namespace, repository, and tag.
    /// Returns false for private-registry references (host contains a '.' or ':').
    /// </summary>
    private static bool TryParseImageRef(
        string imageRef,
        out string ns,
        out string repo,
        out string tag)
    {
        ns = repo = tag = string.Empty;

        var colonIdx = imageRef.LastIndexOf(':');
        var imageNoTag = colonIdx > 0 ? imageRef[..colonIdx] : imageRef;
        tag = colonIdx > 0 ? imageRef[(colonIdx + 1)..] : "latest";

        var slashIdx = imageNoTag.IndexOf('/');

        if (slashIdx < 0)
        {
            // Official image like "python"
            ns = "library";
            repo = imageNoTag;
            return !string.IsNullOrWhiteSpace(repo);
        }

        var possibleHost = imageNoTag[..slashIdx];

        // A segment containing '.' or ':' is a hostname — skip private registries
        if (possibleHost.Contains('.') || possibleHost.Contains(':'))
        {
            return false;
        }

        ns = possibleHost;
        repo = imageNoTag[(slashIdx + 1)..];
        return !string.IsNullOrWhiteSpace(ns) && !string.IsNullOrWhiteSpace(repo);
    }

    private static UpdateCheckResult Err(AppRecord app, string msg)
        => new(app.Name, UpdateMethod.Specialised, false, app.InstalledVersion, null, msg);
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
internal sealed partial class DockerHubJsonContext : JsonSerializerContext;

