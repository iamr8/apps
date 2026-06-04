using System.CommandLine;
using System.Reflection;
using System.Runtime.InteropServices;

using apps.Components;
using apps.Components.Audit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Serilog;

namespace apps;

internal static class Program
{
    public static readonly string Version;

    static Program()
    {
        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        Version = version?.InformationalVersion.Split('+')[0] ?? "unknown";
    }

    private static async Task<int> Main(string[] args)
    {
        EnsureSafeWorkingDirectory();

        SerilogConfigurator.Configure();
        var services = new ServiceCollection();

        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddSerilog(dispose: false);
            b.SetMinimumLevel(LogLevel.Trace);
        });

        services.AddSingleton<PinManager>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<PlistReader>();
        services.AddSingleton<LiveProgressRenderer>();
        services.AddSingleton<ProjectManifestFinder>();
        services.AddSingleton<ConnectionWarmup>();

        services.AddAllComponents();
        services.AddAuditComponent();

        // Self-update download client: generous timeouts for streaming the multi-MB release archive.
        services.AddCheckerClient("github-download", "https://github.com", 4, totalTimeoutSeconds: 300, attemptTimeoutSeconds: 120);

        services.AddSingleton<ScanOrchestrator>();
        services.AddSingleton<CheckOrchestrator>();
        services.AddSingleton<Orchestrator>();

        await using var serviceProvider = services.BuildServiceProvider();

        using var appLifetime = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            appLifetime.Cancel();
        };

        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => appLifetime.Cancel());

        var rootCmd = new RootCommand("apps — discover and check for updates on macOS");

        var orch = serviceProvider.GetRequiredService<Orchestrator>();
        UpdateCommand.Configure(rootCmd, orch, serviceProvider);

        int exitCode;
        try
        {
            exitCode = await rootCmd.Parse(args).InvokeAsync(cancellationToken: appLifetime.Token);
        }
        catch (OperationCanceledException)
        {
            exitCode = 130;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }

        return exitCode;
    }

    /// <summary>
    /// Ensures the process has a valid working directory. If the current directory has been
    /// deleted (e.g., a temp folder), subprocesses like brew, npm, and dotnet crash with
    /// <c>getcwd: No such file or directory</c>. Switching to $HOME prevents this.
    /// </summary>
    private static void EnsureSafeWorkingDirectory()
    {
        try
        {
            _ = Directory.GetCurrentDirectory();
        }
        catch
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Environment.CurrentDirectory = home;
        }
    }
}