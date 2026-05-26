using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Docker;

/// <summary>
/// Discovers local Docker images via <c>docker images</c>.
/// One <see cref="AppKind.Packages"/> entry is emitted per unique repository:tag pair.
/// <see cref="DiscoveredApp.Name"/> is the full <c>repo:tag</c> reference (e.g. <c>postgres:18</c>)
/// so that each image has a unique identity key throughout the pipeline.
/// <see cref="DiscoveredApp.InstalledVersion"/> is the tag (e.g. <c>18</c>) and is shown
/// in the version column; the renderer strips the tag suffix from the name for display.
/// <see cref="DiscoveredApp.Digest"/> holds the sha256 so <see cref="Checkers.DockerHubChecker"/>
/// can detect content changes behind a stable tag.
/// Images with no registry digest (locally built) are skipped.
/// The full image reference is also stored in <see cref="DiscoveredApp.SuggestedMethodDetail"/>.
/// </summary>
public sealed class DockerImageScanner(IProcessRunner runner, ILogger<DockerImageScanner> logger)
    : IScanner
{
    private string? _executablePath;

    public string Name => "Docker";

    /// <inheritdoc/>
    public string DisplayName => "Docker";

    public OS SupportedOS => OS.MacOS | OS.Windows;

    /// <inheritdoc/>
    public string? GetSourceQualifier(AppKind kind) => "Image";

    public bool IsAvailable()
    {
        // TODO: needs fix on Windows
        _executablePath = ScannerHelper.FindExecutable("docker");
        if (_executablePath is not null)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // Honour DOCKER_HOST when it points to a Unix socket — the docker binary uses it
            // and we should probe the same path the client will actually connect to.
            var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
            if (dockerHost?.StartsWith("unix://", StringComparison.OrdinalIgnoreCase) == true)
            {
                var socketPath = dockerHost["unix://".Length..];
                if (!string.IsNullOrWhiteSpace(socketPath) && IsSocketListening(socketPath))
                {
                    return true;
                }
            }

            string[] knownSockets =
            [
                "/var/run/docker.sock",
                Path.Combine(home, ".docker", "run", "docker.sock"),
                Path.Combine(home, ".orbstack", "run", "docker.sock"),
                Path.Combine(home, "Library", "Containers", "com.docker.docker", "Data", "docker.raw.sock")
            ];

            // File.Exists alone is unreliable: the socket file may persist after the daemon
            // stops (OrbStack and Docker Desktop both leave socket paths behind). Actually
            // connecting to the socket is a zero-overhead probe that is definitively correct.
            if (knownSockets.Any(IsSocketListening))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public bool StripTagFromDisplayName => true;

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Use | as the column delimiter — it cannot appear in repository names, tags, or
        // digests (sha256:…). The original \t approach broke because Process passes \t as
        // the two characters '\' and 't', not a real tab, while the C# split used '\t' (tab).
        const string format = @"--format {{.Repository}}|{{.Tag}}|{{.Digest}}|{{.ID}}";
        var result = await runner.RunAsync(_executablePath!, $"images {format}", cancellationToken);

        if (!result.Success)
        {
            logger.LogWarning("'docker images' failed ({Code}): {Err}", result.ExitCode, result.StandardError.Trim());
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var app = ParseLine(line, seen);
            if (app is not null)
            {
                yield return app;
            }
        }
    }

    private DiscoveredApp? ParseLine(string line, HashSet<string> seen)
    {
        var parts = line.Split('|', 4);
        if (parts.Length < 4)
        {
            return null;
        }

        var repo = parts[0].Trim();
        var tag = parts[1].Trim();

        // Skip untagged / intermediate images
        if (repo == "<none>" || tag == "<none>")
        {
            return null;
        }

        var imageRef = $"{repo}:{tag}";
        if (!seen.Add(imageRef))
        {
            return null; // deduplicate
        }

        var digest = parts[2].Trim() is { Length: > 0 } d && d != "<none>" ? d : null;

        // Skip locally-built images — they have no registry digest to compare against.
        if (digest is null)
        {
            return null;
        }

        return new DiscoveredApp(
            imageRef,
            new AppIdentifier(Name, DisplayName, "Image"),
            AppKind.Packages,
            tag,
            SuggestedMethod: UpdateMethod.Specialised,
            SuggestedMethodDetail: imageRef,
            Digest: digest);
    }

    /// <summary>
    /// Tries to open a connection to a Unix domain socket.
    /// Returns <see langword="true"/> when the daemon is accepting connections, <see langword="false"/> otherwise.
    /// The connect call is synchronous but completes in microseconds for local sockets.
    /// Skips the <c>File.Exists</c> pre-check — socket files are special filesystem entries
    /// that may not report correctly through <c>File.Exists</c> on all platforms.
    /// </summary>
    private static bool IsSocketListening(string path)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch
        {
            return false;
        }
    }
}