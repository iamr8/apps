using System.CommandLine;
using System.Diagnostics;

using apps.Infrastructure;
using apps.Models;
using apps.Orchestration;

namespace apps.Commands;

/// <summary>
/// Configures the root command with all CLI options.
/// Scans apps, checks for updates, and displays results.
/// By default only outdated apps are shown; use <c>--all</c> or <c>--kind</c> to broaden the view.
/// </summary>
public static class UpdateCommand
{
    private const string InstallPath = "/usr/local/bin/apps";

    /// <summary>Configures the root command with update options and action.</summary>
    public static void Configure(RootCommand rootCmd, UpdateOrchestrator orchestrator)
    {
        var allOpt = new Option<bool>("--all", new[] { "-a" })
        {
            Description = "Show all apps, not just outdated ones"
        };

        var kindOpt = new Option<string?>("--kind", new[] { "-k" })
        {
            Description = "Scope to one app kind: app | package | lib | dep | service | ext"
        };
        kindOpt.AcceptOnlyFromAmong("app", "package", "lib", "dep", "service", "ext");

        var dryRunOpt = new Option<bool>("--dry-run", new[] { "-d" })
        {
            Description = "Scan only — show discovered apps without checking for updates"
        };

        var pinOpt = new Option<string?>("--pin", new[] { "-p" })
        {
            Description = "Pin a package at its current version to suppress update notifications"
        };

        var unpinOpt = new Option<string?>("--unpin")
        {
            Description = "Remove a pin from a package so it appears in update results again"
        };

        var installOpt = new Option<bool>("--install")
        {
            Description = "Install apps to /usr/local/bin so it can be run from anywhere"
        };

        rootCmd.Options.Add(allOpt);
        rootCmd.Options.Add(kindOpt);
        rootCmd.Options.Add(dryRunOpt);
        rootCmd.Options.Add(pinOpt);
        rootCmd.Options.Add(unpinOpt);
        rootCmd.Options.Add(installOpt);

        rootCmd.SetAction(async (pr, cancellationToken) =>
        {
            if (pr.GetValue(installOpt))
            {
                return HandleInstall();
            }

            var pinValue = pr.GetValue(pinOpt);
            var unpinValue = pr.GetValue(unpinOpt);
            var allValue = pr.GetValue(allOpt);
            var dryRunValue = pr.GetValue(dryRunOpt);
            var kindStr = pr.GetValue(kindOpt);

            if (pinValue is not null || unpinValue is not null)
            {
                if (allValue || dryRunValue || kindStr is not null)
                {
                    await Console.Error.WriteLineAsync(
                        "Error: --pin and --unpin cannot be combined with --all, --kind, or --dry-run.");
                    return 1;
                }

                if (pinValue is not null && unpinValue is not null)
                {
                    await Console.Error.WriteLineAsync(
                        "Error: --pin and --unpin cannot be used together.");
                    return 1;
                }
            }

            AppKind? kind = null;

            if (kindStr is not null)
            {
                if (!AppKindExtensions.TryParseCliString(kindStr, out var k))
                {
                    await Console.Error.WriteLineAsync(
                        $"Unknown kind '{kindStr}'. Valid: app | package | lib | dep | service | ext");
                    return 1;
                }

                kind = k;
            }

            LiveProgressRenderer.RenderClear();

            var options = new UpdateOptions
            {
                ScopeKind = kind,
                ShowAll = allValue || kind is not null,
                DryRun = dryRunValue,
                PinPackage = pinValue,
                UnpinPackage = unpinValue
            };

            return await orchestrator.RunFullPipelineAsync(options, cancellationToken);
        });
    }

    private static int HandleInstall()
    {
        var currentExe = Environment.ProcessPath;

        if (string.IsNullOrEmpty(currentExe))
        {
            Console.Error.WriteLine("Error: could not determine the current executable path.");
            return 1;
        }

        if (string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(InstallPath), StringComparison.Ordinal))
        {
            Console.WriteLine($"Already installed at {InstallPath}.");
            return 0;
        }

        try
        {
            File.Copy(currentExe, InstallPath, overwrite: true);
#pragma warning disable CA1416
            File.SetUnixFileMode(InstallPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
            Console.WriteLine($"Installed to {InstallPath}");
            Console.WriteLine("You can now run 'apps' from any directory.");
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Permission denied. Try: sudo {currentExe} --install");
            return 1;
        }
    }
}