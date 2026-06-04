using System.Diagnostics;

using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace apps;

/// <summary>
/// Builds and configures the global Serilog logger.
///
/// <para>
/// Logging and console output are kept strictly separate:
///   • <see cref="ILogger"/> / Serilog writes <b>only</b> to the log file — never to the console.
///   • Everything the user sees on the console is written explicitly through
///     <see cref="LiveProgressRenderer"/> via <c>Console.Write</c> / <c>Console.WriteLine</c>.
/// This avoids per-source filtering: log noise lives in the file, the console stays curated.
/// </para>
///
/// Sink:
///   • File — Verbose (all levels), daily rolling.
///            Path: ~/.local/share/apps/log/apps-YYYYMMDD.log
///            Format: structured text with SourceContext + Caller enrichment.
///
/// SourceContext note:
///   When the Microsoft ILogger&lt;T&gt; bridge is used (via Serilog.Extensions.Logging),
///   ILogger&lt;MyClass&gt;.Log(…) automatically enriches the event with
///   SourceContext = "apps.Infrastructure.MyClass" — the full CLR type name.
///   The file template renders it as a short name.
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
    /// Creates the global Serilog logger wired to the rolling file sink only.
    /// Call <see cref="Log.CloseAndFlushAsync"/> on app shutdown.
    /// </summary>
    public static void Configure()
    {
        var logDirectory = Debugger.IsAttached
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "apps-logs")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "apps", "log");

        Directory.CreateDirectory(logDirectory);
        var logFilePath = Path.Combine(logDirectory, "apps-.log");

        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext() // picks up CallerExtensions pushes
            .Enrich.With<ShortSourceContextEnricher>() // adds ShortSourceContext property
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
    }
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
        var shortName = dot >= 0 ? full[(dot + 1)..] : full;

        logEvent.AddOrUpdateProperty(
            factory.CreateProperty("ShortSourceContext", shortName));
    }
}
