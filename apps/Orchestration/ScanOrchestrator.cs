using System.Threading.Channels;

using apps.Infrastructure;
using apps.Models;
using apps.Scanners;

using Microsoft.Extensions.Logging;

namespace apps.Orchestration;

/// <summary>
/// Stage 1 of the pipeline: runs all available scanners concurrently and
/// streams discovered apps through a bounded channel into an in-memory list.
///
/// All scanners launch simultaneously; the channel provides back-pressure
/// so fast scanners naturally throttle when the consumer is busy.
/// Connection warm-up runs in parallel with scanning to pre-establish
/// TLS connections for the check phase.
/// </summary>
public sealed class ScanOrchestrator(
    IEnumerable<IScanner> scanners,
    ConnectionWarmup warmup,
    LiveProgressRenderer renderer,
    ILogger<ScanOrchestrator> logger)
{
    private const int ChannelCapacity = 512;

    /// <summary>
    /// Runs all available scanners concurrently and returns every discovered app.
    /// Project-level scanners are excluded.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredApp>> RunAsync(CancellationToken cancellationToken = default)
    {
        var activeScanners = scanners
            .Where(s =>
            {
                if (s is IProjectLevelScanner)
                {
                    logger.LogDebug("Skipping project-level scanner {Name}", s.Name);
                    return false;
                }

                if (!s.IsAvailable())
                {
                    logger.LogDebug("Scanner {Name} is not available, skipping", s.Name);
                    return false;
                }

                return true;
            })
            .ToList();

        if (activeScanners.Count == 0)
        {
            logger.LogWarning("No scanners are available");
            return [];
        }

        renderer.SetScannerCount(activeScanners.Count);

        var channel = Channel.CreateBounded<DiscoveredApp>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        // Pre-establish HTTP connections to registry hosts while scanners run.
        var warmupTask = warmup.WarmAsync(cancellationToken);

        var producerTask = Task.WhenAll(activeScanners.Select(s => RunScannerAsync(s, channel.Writer, cancellationToken)))
            .ContinueWith(t =>
            {
                // Observe the faulted task to prevent UnobservedTaskException in GC.
                _ = t.Exception;
                channel.Writer.TryComplete();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        var results = await DrainAsync(channel.Reader, cancellationToken);

        await producerTask.ConfigureAwait(false);
        await warmupTask.ConfigureAwait(false);

        renderer.RenderScanComplete(results.Count);
        logger.LogInformation("Scan complete: {Total} apps discovered", results.Count);
        return results;
    }

    private async Task RunScannerAsync(IScanner scanner, ChannelWriter<DiscoveredApp> writer, CancellationToken cancellationToken)
    {
        renderer.RenderScannerActive(scanner.Name);

        try
        {
            await foreach (var app in scanner.ScanAsync(cancellationToken).ConfigureAwait(false))
            {
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

    private static async Task<List<DiscoveredApp>> DrainAsync(ChannelReader<DiscoveredApp> reader, CancellationToken cancellationToken)
    {
        var results = new List<DiscoveredApp>(256);

        await foreach (var app in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(app);
        }

        return results;
    }
}