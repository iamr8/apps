using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;

namespace apps.Logging;

/// <summary>
/// Serilog sink that routes console output through <see cref="LiveProgressRenderer"/>
/// so log messages properly clear any in-progress ANSI scan line before writing,
/// then restore it afterward. This prevents log lines from being appended to the
/// same terminal line as the "● Scanning …" indicator.
///
/// Set <see cref="Renderer"/> after the DI container is built.
/// Until then the sink falls back to writing directly to stderr.
/// </summary>
public sealed class RendererConsoleSink : ILogEventSink
{
    private readonly ITextFormatter _formatter;

    /// <summary>
    /// Set to the DI-resolved <see cref="LiveProgressRenderer"/> after the container is built.
    /// Thread-safe: only written once before any scanning begins.
    /// </summary>
    public LiveProgressRenderer? Renderer { get; set; }

    /// <summary>Initialises the sink with the formatter used to convert log events to text.</summary>
    public RendererConsoleSink(ITextFormatter formatter)
    {
        _formatter = formatter;
    }

    /// <inheritdoc/>
    public void Emit(LogEvent logEvent)
    {
        using var sw = new StringWriter();
        _formatter.Format(logEvent, sw);
        var text = sw.ToString().TrimEnd('\r', '\n');

        if (Renderer is not null)
        {
            Renderer.WriteLogLine(text);
        }
        else
        {
            Console.Error.WriteLine(text);
        }
    }
}

