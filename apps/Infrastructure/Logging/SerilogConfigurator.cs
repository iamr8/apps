using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace apps.Infrastructure.Logging;

/// <summary>
/// Builds and configures the global Serilog logger.
///
/// Sinks:
///   • Console  — Warning+ by default; switches to Debug+ when --verbose is active.
///               Plain theme for TTY; no-theme when redirected.
///   • File     — Verbose (all levels), daily rolling.
///               Path: ~/.local/share/apps/log/apps-YYYYMMDD.log
///               Format: structured text with SourceContext + Caller enrichment.
///
/// SourceContext note:
///   When the Microsoft ILogger<T> bridge is used (via Serilog.Extensions.Logging),
///   ILogger<MyClass>.Log(…) automatically enriches the event with
///   SourceContext = "apps.Infrastructure.MyClass" — the full CLR type name.
///   The file template renders it as a short name; the console omits it for readability.
///
/// Caller note:
///   The LoggerCallerExtensions helpers push a "Caller" property to the LogContext
///   (file + line + method) using C# compiler attributes — zero overhead, no stack walking.
/// </summary>
public static class SerilogConfigurator
{
    /// <summary>
    /// File: timestamp · level · short type name · optional Caller · message · exception
    /// </summary>
    private const string FileTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {ShortSourceContext,-40} " +
        "{Caller}| {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Console: minimal — level and message only; no type noise.
    /// </summary>
    private const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";


    /// <summary>
    /// Creates the Serilog logger, wires the supplied <paramref name="consoleSink"/> for
    /// console output, and returns the <see cref="LoggingLevelSwitch"/> that controls the
    /// console minimum level at runtime.
    /// Set <see cref="RendererConsoleSink.Renderer"/> after the DI container is built.
    /// Call <see cref="Log.CloseAndFlushAsync"/> on app shutdown.
    /// </summary>
    public static LoggingLevelSwitch Configure(string logDirectory, RendererConsoleSink consoleSink)
    {
        Directory.CreateDirectory(logDirectory);
        var logFilePath = Path.Combine(logDirectory, "apps-.log");

        var consoleLevelSwitch = new LoggingLevelSwitch(LogEventLevel.Warning);

        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext() // picks up CallerExtensions pushes
            .Enrich.With<ShortSourceContextEnricher>() // adds ShortSourceContext property
            .WriteTo.Sink(consoleSink, levelSwitch: consoleLevelSwitch)
            .WriteTo.File(
                logFilePath,
                outputTemplate: FileTemplate,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                restrictedToMinimumLevel: LogEventLevel.Verbose,
                shared: false,
                flushToDiskInterval: TimeSpan.FromSeconds(2))
            .MinimumLevel.Verbose()
            .CreateLogger();

        return consoleLevelSwitch;
    }

    /// <summary>Creates the <see cref="RendererConsoleSink"/> with the standard console formatter.</summary>
    public static RendererConsoleSink CreateConsoleSink()
        => new(new MessageTemplateTextFormatter(ConsoleTemplate));
}

/// <summary>
/// Enriches log events with <c>ShortSourceContext</c> — just the class name,
/// without the namespace prefix (e.g. "ProcessRunner" instead of
/// "apps.Infrastructure.ProcessRunner").
/// </summary>
file sealed class ShortSourceContextEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
        if (!logEvent.Properties.TryGetValue("SourceContext", out var prop))
            return;

        var full = prop.ToString().Trim('"');
        var dot = full.LastIndexOf('.');
        var short_ = dot >= 0 ? full[(dot + 1)..] : full;

        logEvent.AddOrUpdateProperty(
            factory.CreateProperty("ShortSourceContext", short_));
    }
}