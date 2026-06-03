using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;

using Serilog.Context;

namespace apps.Logging;

/// <summary>
/// Extension methods for <see cref="ILogger"/> that capture the call site
/// (file name, line number, method name) at compile time using C# caller-info
/// attributes — zero runtime overhead, no stack walking.
///
/// The captured info is pushed as a <c>Caller</c> property into Serilog's
/// <see cref="LogContext"/> and included in the file log template.
///
/// Usage:
/// <code>
///   _logger.DebugCaller("Starting scan");
///   _logger.VerboseCaller("Parsed plist at {Path}", path);
/// </code>
///
/// The console sink omits <c>Caller</c> for clean output; the file sink includes
/// it so verbose log viewers show exactly which file+line emitted each message.
/// </summary>
public static class LoggerCallerExtensions
{
    public static void TraceWithCaller(
        this ILogger logger,
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int line = 0)
    {
        if (!logger.IsEnabled(LogLevel.Trace)) return;
        using (PushCaller(member, filePath, line))
            logger.LogTrace(message);
    }

    public static void TraceWithCaller<T0>(
        this ILogger logger,
        string message, T0 arg0,
        [CallerMemberName] string member = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int line = 0)
    {
        if (!logger.IsEnabled(LogLevel.Trace)) return;
        using (PushCaller(member, filePath, line))
            logger.LogTrace(message, arg0);
    }

    public static void TraceWithCaller<T0, T1>(
        this ILogger logger,
        string message, T0 arg0, T1 arg1,
        [CallerMemberName] string member = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int line = 0)
    {
        if (!logger.IsEnabled(LogLevel.Trace)) return;
        using (PushCaller(member, filePath, line))
            logger.LogTrace(message, arg0, arg1);
    }


    public static void DebugWithCaller(
        this ILogger logger,
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int line = 0)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        using (PushCaller(member, filePath, line))
            logger.LogDebug(message);
    }

    public static void DebugWithCaller<T0>(
        this ILogger logger,
        string message, T0 arg0,
        [CallerMemberName] string member = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int line = 0)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        using (PushCaller(member, filePath, line))
            logger.LogDebug(message, arg0);
    }

    public static void DebugWithCaller<T0, T1>(
        this ILogger logger,
        string message, T0 arg0, T1 arg1,
        [CallerMemberName] string member = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int line = 0)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        using (PushCaller(member, filePath, line))
            logger.LogDebug(message, arg0, arg1);
    }

    public static void DebugWithCaller<T0, T1, T2>(
        this ILogger logger,
        string message, T0 arg0, T1 arg1, T2 arg2,
        [CallerMemberName] string member = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int line = 0)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        using (PushCaller(member, filePath, line))
            logger.LogDebug(message, arg0, arg1, arg2);
    }


    public static void InfoWithCaller(
        this ILogger logger,
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int line = 0)
    {
        if (!logger.IsEnabled(LogLevel.Information)) return;
        using (PushCaller(member, filePath, line))
            logger.LogInformation(message);
    }

    public static void InfoWithCaller<T0>(
        this ILogger logger,
        string message, T0 arg0,
        [CallerMemberName] string member = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int line = 0)
    {
        if (!logger.IsEnabled(LogLevel.Information)) return;
        using (PushCaller(member, filePath, line))
            logger.LogInformation(message, arg0);
    }


    /// <summary>
    /// Pushes a <c>Caller</c> property of the form "FileName.cs:42 MethodName()"
    /// into Serilog's ambient LogContext.  The using-block scope pops it when done.
    /// </summary>
    private static IDisposable PushCaller(string member, string filePath, int line)
    {
        var caller = $"{Path.GetFileName(filePath)}:{line} {member}() ";
        return LogContext.PushProperty("Caller", caller);
    }
}