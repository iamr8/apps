using System.Threading.Channels;

using apps.Components;

using Microsoft.Extensions.Logging;

namespace apps;

/// <summary>
/// Stage 1 of the pipeline: runs all available scanners concurrently and
/// streams discovered apps through a bounded channel into an in-memory list.
///
/// All scanners launch simultaneously; the channel provides back-pressure
/// so fast scanners naturally throttle when the consumer is busy.
/// Connection warm-up runs in parallel with scanning to pre-establish
/// TLS connections for the check phase.
/// </summary>
public sealed class ScanOrchestrator(IEnumerable<IScanner> scanners, ConnectionWarmup warmup, LiveProgressRenderer renderer, ILogger<ScanOrchestrator> logger)
{
    private const int ChannelCapacity = 512;

    /// <summary>
    /// Runs all available scanners concurrently and returns every discovered app.
    /// Project-level scanners are excluded.
    /// </summary>
    public async Task<Dictionary<string, DiscoveredApp>> ScanAsync(AppKind? kind, CancellationToken cancellationToken = default)
    {
        var activeScanners = GetActiveScanners(kind);
        if (activeScanners.Length == 0)
        {
            logger.LogWarning("No scanners are available");
            return [];
        }

        renderer.SetScannerCount(activeScanners.Length);

        // Pre-establish HTTP connections to registry hosts while scanners run.
        var warmupTask = warmup.WarmAsync(cancellationToken);

        // Periodic timer to refresh the scan progress line with updated elapsed time
        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timerTask = renderer.RunScanTimerAsync(timerCts.Token);

        var results = new Dictionary<string, DiscoveredApp>(256);
        await foreach (var app in activeScanners.WhenAll<IScanner, DiscoveredApp>(RunScannerAsync, cancellationToken: cancellationToken))
        {
            // De-duplicate by source, kind AND name. Two discoveries collapse only when the same
            // scanner reports the same name and kind; a same-named app and extension (the "Claude"
            // desktop app vs the "Claude" Chrome extension) or two same-kind extensions from
            // different ecosystems (the "EditorConfig" JetBrains plugin vs the VS Code extension)
            // are different things and must stay separate top-level entries.
            var key = DedupKey(app.Source.Name, app.Kind, app.Name);
            if (results.TryGetValue(key, out var existing))
            {
                existing = existing with
                {
                    Description = existing.Description ?? app.Description,
                    SubApps = (existing.SubApps ?? []).Append(app with { IsDuplicate = true, Description = null }).ToList()
                };
                results[key] = existing;
            }
            else
            {
                results.Add(key, app);
            }
        }

        await warmupTask.ConfigureAwait(false);

        await timerCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await timerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        // Count sub-apps too (e.g. a Homebrew cask channel of a scanned bundle): they are
        // discovered here and checked later, so both totals cover the same set.
        var discoveredCount = results.Values.Sum(a => 1 + (a.SubApps?.Count ?? 0));
        renderer.RenderScanComplete(discoveredCount);
        logger.LogInformation("Scan complete: {Total} apps discovered", discoveredCount);
        return results;
    }

    public async Task<DiscoveredApp?> FindAsync(string packageName, CancellationToken cancellationToken = default)
    {
        // TODO: needs optimization
        var discovered = await ScanAsync(kind: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        var match = discovered.FirstOrDefault(a => string.Equals(a.Value.Name, packageName, StringComparison.OrdinalIgnoreCase));
        return match.Value;
    }

    private IScanner[] GetActiveScanners(AppKind? kind)
    {
        var activeScanners = scanners
            .Where(s =>
            {
                if (OperatingSystem.IsWindows() && !s.SupportedOS.HasFlag(OS.Windows))
                {
                    logger.LogDebug("Scanner {Name} does not support Windows, skipping", s.Name);
                    return false;
                }

                if (OperatingSystem.IsMacOS() && !s.SupportedOS.HasFlag(OS.MacOS))
                {
                    logger.LogDebug("Scanner {Name} does not support macOS, skipping", s.Name);
                    return false;
                }

                if (kind.HasValue && !s.Kind.HasFlag(kind.Value))
                {
                    logger.LogDebug("Scanner {Name} does not support AppKind {Kind}, skipping", s.Name, kind.Value);
                    return false;
                }

                if (!s.IsAvailable())
                {
                    logger.LogDebug("Scanner {Name} is not available, skipping", s.Name);
                    return false;
                }

                return true;
            })
            .ToArray();
        return activeScanners;
    }

    private async Task RunScannerAsync(IScanner scanner, ChannelWriter<DiscoveredApp> writer, CancellationToken cancellationToken)
    {
        renderer.RenderScannerActive(scanner.Name);

        try
        {
            await foreach (var app in scanner.ScanAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!scanner.Kind.HasFlag(app.Kind))
                {
                    throw new InvalidOperationException($"App {app.Name} is of kind {app.Kind} but scanner {scanner.Name} only supports {scanner.Kind}");
                }

                await writer.WriteAsync(app, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scanner {Name} failed", scanner.Name);
            renderer.RenderError($"Scanner {scanner.Name}: {ex.Message}");
        }

        renderer.RenderScannerDone(scanner.Name);
    }

    /// <summary>Composite de-duplication key: two discoveries collapse only when they share the
    /// same source scanner, kind and name, so same-named items from different scanners (e.g. a
    /// JetBrains plugin vs a VS Code extension) or of different kinds stay distinct top-level
    /// entries.</summary>
    private static string DedupKey(string source, AppKind kind, string name) => $"{source}|{kind}|{name}";
}