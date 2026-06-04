using System.Diagnostics;

namespace apps;

/// <summary>
/// Renders an in-place download progress line — percentage, transferred/total size, and transfer
/// rate — to stdout while a release archive streams in. Repaints are throttled to roughly ten per
/// second. On a non-TTY it stays silent. Call <see cref="Complete"/> once the transfer finishes to
/// paint the final 100% line and terminate it with a newline.
/// </summary>
internal sealed class DownloadProgressReporter(string version, string rid, long? totalBytes)
{
    private static readonly TimeSpan RepaintInterval = TimeSpan.FromMilliseconds(100);

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private TimeSpan _lastPaint;
    private bool _painted;

    /// <summary>Repaints the progress line for the given cumulative byte count, throttled by time.</summary>
    public void Report(long transferred)
    {
        var elapsed = _stopwatch.Elapsed;
        if (_painted && elapsed - _lastPaint < RepaintInterval)
        {
            return;
        }

        _painted = true;
        _lastPaint = elapsed;
        Paint(transferred, elapsed, final: false);
    }

    /// <summary>Paints the final 100% line and moves to the next line.</summary>
    public void Complete(long transferred)
    {
        Paint(transferred, _stopwatch.Elapsed, final: true);
    }

    private void Paint(long transferred, TimeSpan elapsed, bool final)
    {
        if (!AnsiStyle.IsAnsi)
        {
            return;
        }

        var rate = elapsed.TotalSeconds > 0 ? transferred / elapsed.TotalSeconds : 0;
        var head = $"{AnsiStyle.Cyan("↓")} apps v{version} ({rid})";

        string line;
        if (totalBytes is > 0)
        {
            var pct = final ? 100 : (int)Math.Clamp(transferred * 100.0 / totalBytes.Value, 0, 100);
            line = $"{head}  {AnsiStyle.ProgressBar(pct, 100)}  {FormatBytes(transferred)}/{FormatBytes(totalBytes.Value)}  {FormatRate(rate)}";
        }
        else
        {
            line = $"{head}  {FormatBytes(transferred)}  {FormatRate(rate)}";
        }

        Console.Out.Write($"\r\e[2K{line}");

        if (final)
        {
            Console.Out.Write('\n');
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.0} {units[unit]}";
    }

    private static string FormatRate(double bytesPerSecond) => $"{FormatBytes((long)bytesPerSecond)}/s";
}
