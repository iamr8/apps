using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace apps;

/// <summary>
/// Performs an in-process self-update of the installed <c>apps</c> binary.
/// Downloads the release archive matching the running architecture, extracts the binary, and
/// atomically renames it over the currently-running executable. On Unix a rename over a running
/// binary is safe — the live process keeps its old inode and the new file takes effect on the next
/// launch — so no external shell script or detached process is required.
/// </summary>
internal static class SelfUpdater
{
    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>
    /// Downloads the release identified by <paramref name="info"/> and replaces the running binary.
    /// Returns <see langword="true"/> on success. All failure paths print a user-facing message and
    /// return <see langword="false"/>.
    /// </summary>
    public static async Task<bool> PerformUpgradeAsync(
        IHttpClientFactory httpClientFactory,
        SelfUpdateInfo info,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            await Console.Error.WriteLineAsync("Self-upgrade is only supported on macOS.").ConfigureAwait(false);
            return false;
        }

        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "osx-arm64",
            Architecture.X64 => "osx-x64",
            _ => null
        };

        if (rid is null)
        {
            await Console.Error.WriteLineAsync($"Unsupported architecture: {RuntimeInformation.ProcessArchitecture}.").ConfigureAwait(false);
            return false;
        }

        var targetPath = ResolveTargetPath();
        if (targetPath is null)
        {
            await Console.Error.WriteLineAsync("Self-upgrade is only available for the installed binary. Run 'apps --install' first, then 'apps --upgrade'.").ConfigureAwait(false);
            return false;
        }

        var assetName = $"apps-{rid}.tar.gz";
        var asset = info.Assets.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.Ordinal));
        if (asset?.DownloadUrl is null)
        {
            await Console.Error.WriteLineAsync($"Release v{info.LatestVersion} has no asset named '{assetName}'.").ConfigureAwait(false);
            return false;
        }

        var targetDir = Path.GetDirectoryName(targetPath)!;
        var tempBinaryPath = Path.Combine(targetDir, $".apps.upgrade.{Environment.ProcessId}.tmp");

        try
        {
            await DownloadAndExtractAsync(
                    httpClientFactory,
                    asset.DownloadUrl,
                    info.LatestVersion!,
                    rid,
                    tempBinaryPath,
                    cancellationToken)
                .ConfigureAwait(false);

            File.SetUnixFileMode(tempBinaryPath, ExecutableMode);

            // Atomic swap: rename the freshly-extracted binary over the running executable.
            // The running process keeps the old inode; the new file is used on the next launch.
            File.Move(tempBinaryPath, targetPath, overwrite: true);

            Console.WriteLine($"\e[32m✓ Updated to v{info.LatestVersion}. Restart 'apps' to use the new version.\e[0m");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(tempBinaryPath);
            await Console.Error.WriteLineAsync($"Permission denied writing to {targetPath}.").ConfigureAwait(false);
            await Console.Error.WriteLineAsync("Try: sudo apps --upgrade").ConfigureAwait(false);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDelete(tempBinaryPath);
            await Console.Error.WriteLineAsync($"Upgrade failed: {ex.Message}").ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Streams the release archive from <paramref name="url"/>, reporting download progress, and
    /// extracts the <c>apps</c> binary to <paramref name="destBinaryPath"/>. <paramref name="version"/>
    /// and <paramref name="rid"/> label the progress line. Throws when the archive lacks the binary.
    /// </summary>
    private static async Task DownloadAndExtractAsync(
        IHttpClientFactory httpClientFactory,
        string url,
        string version,
        string rid,
        string destBinaryPath,
        CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("github-download");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var reporter = new DownloadProgressReporter(version, rid, response.Content.Headers.ContentLength);
        if (!AnsiStyle.IsAnsi)
        {
            Console.WriteLine($"↓ Downloading apps v{version} ({rid})…");
        }

        await using var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var progressStream = new ProgressStream(networkStream, reporter.Report);
        await using var gzip = new GZipStream(progressStream, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);

        while (await tar.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
            {
                continue;
            }

            var name = entry.Name.TrimStart('.', '/');
            if (!name.Equals("apps", StringComparison.Ordinal) && !name.EndsWith("/apps", StringComparison.Ordinal))
            {
                continue;
            }

            await using var output = new FileStream(destBinaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await entry.DataStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            reporter.Complete(progressStream.TotalRead);
            return;
        }

        throw new InvalidOperationException("Downloaded archive did not contain the 'apps' binary.");
    }

    /// <summary>
    /// Returns the full path of the running binary when it is the native <c>apps</c> executable, or
    /// <see langword="null"/> when running under a host (e.g. <c>dotnet run</c>) where self-replacement
    /// does not apply.
    /// </summary>
    private static string? ResolveTargetPath()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return null;
        }

        if (!Path.GetFileName(exe).Equals("apps", StringComparison.Ordinal))
        {
            return null;
        }

        return Path.GetFullPath(exe);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup of the temp file; ignore failures.
        }
    }
}
