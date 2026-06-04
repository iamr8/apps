using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

namespace apps;

/// <summary>
/// Configures the root command with all CLI options.
/// Scans apps, checks for updates, and displays results.
/// By default only outdated apps are shown; use <c>--all</c> or <c>--kind</c> to broaden the view.
/// </summary>
public static class UpdateCommand
{
    private const string InstallPath = "/usr/local/bin/apps";

    private static readonly Option<bool> OPTION_all = new("--all", "-a")
    {
        Description = "Show all apps, not just outdated ones"
    };

    private static readonly Option<string?> OPTION_kind = new("--kind", "-k")
    {
        Description = "Scope to one app kind: app | package | lib | dep | service | ext",
    };

    private static readonly Option<bool> OPTION_dryRun = new("--dry-run", "-d")
    {
        Description = "Scan only — show discovered apps without checking for updates"
    };

    private static readonly Option<string?> OPTION_pin = new("--pin", "-p")
    {
        Description = "Pin a package at its current version to suppress update notifications"
    };

    private static readonly Option<string?> OPTION_unpin = new("--unpin")
    {
        Description = "Remove a pin from a package so it appears in update results again"
    };

    private static readonly Option<bool> OPTION_install = new("--install")
    {
        Description = "Install \"apps\" to /usr/local/bin so it can be run from anywhere"
    };

    private static readonly Option<bool> OPTION_upgrade = new("--upgrade", "-u")
    {
        Description = "Update apps to the latest version if a newer one is available"
    };

    private static readonly Option<bool> OPTION_version = new("--version", "-v")
    {
        Description = "Show the current version of apps"
    };

    /// <summary>Configures the root command with update options and action.</summary>
    public static void Configure(RootCommand rootCmd, Orchestrator orchestrator, IServiceProvider serviceProvider)
    {
        OPTION_kind.AcceptOnlyFromAmong("app", "package", "lib", "dep", "service", "ext");
        OPTION_kind.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string?>();
            if (value is null || AppKindExtensions.TryParseCliString(value, out _))
            {
                return;
            }

            result.AddError($"Invalid value '{value}' for --kind. Valid options: {string.Join(" | ", Enum.GetValues<AppKind>().Select(c => c.ToCliString()))}");
        });

        rootCmd.Options.Add(OPTION_all);
        rootCmd.Options.Add(OPTION_kind);
        rootCmd.Options.Add(OPTION_dryRun);
        rootCmd.Options.Add(OPTION_pin);
        rootCmd.Options.Add(OPTION_unpin);
        rootCmd.Options.Add(OPTION_install);
        rootCmd.Options.Add(OPTION_upgrade);

        // RootCommand ships a built-in --version option; remove it so our own
        // --version / -v takes over with consistent output and the -v alias.
        var builtInVersion = rootCmd.Options.FirstOrDefault(o => o.Name is "--version");
        if (builtInVersion is not null)
        {
            rootCmd.Options.Remove(builtInVersion);
        }

        rootCmd.Options.Add(OPTION_version);

        rootCmd.SetAction(async (parseResult, cancellationToken) =>
        {
            if (parseResult.GetValue(OPTION_version))
            {
                Console.WriteLine($"apps v{SelfUpdateChecker.CurrentVersion}");
                return 0;
            }

            if (parseResult.GetValue(OPTION_upgrade))
            {
                Console.WriteLine($"apps v{SelfUpdateChecker.CurrentVersion}");
                var httpFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
                var info = await SelfUpdateChecker.FetchLatestReleaseAsync(httpFactory, cancellationToken);

                switch (info.Result)
                {
                    case SelfUpdateResult.UpToDate:
                        Console.WriteLine($"\e[32m✓ You're on the latest version.\e[0m");
                        return 0;
                    case SelfUpdateResult.CheckFailed:
                        Console.WriteLine("Couldn't check for updates — try again later.");
                        return 1;
                    case SelfUpdateResult.UpdateAvailable:
                        Console.WriteLine($"\e[33m⚡ A new version is available: v{info.LatestVersion} (current: v{SelfUpdateChecker.CurrentVersion})\e[0m");
                        PrintChangelog(info.Changelog);
                        var upgraded = await SelfUpdater.PerformUpgradeAsync(httpFactory, info, cancellationToken);
                        return upgraded ? 0 : 1;
                }

                return 0;
            }

            if (parseResult.GetValue(OPTION_install))
            {
                return HandleInstallToShell();
            }

            var pinArg = parseResult.GetValue(OPTION_pin);
            var unpinArg = parseResult.GetValue(OPTION_unpin);
            var allArg = parseResult.GetValue(OPTION_all);
            var dryRunArg = parseResult.GetValue(OPTION_dryRun);
            var kindArg = parseResult.GetValue(OPTION_kind);

            if (pinArg is not null || unpinArg is not null)
            {
                if (allArg || dryRunArg || kindArg is not null)
                {
                    await Console.Error.WriteLineAsync("Error: --pin and --unpin cannot be combined with --all, --kind, or --dry-run.");
                    return 1;
                }

                if (pinArg is not null && unpinArg is not null)
                {
                    await Console.Error.WriteLineAsync("Error: --pin and --unpin cannot be used together.");
                    return 1;
                }
            }

            AppKind? kind = null;
            if (kindArg is not null)
            {
                if (!AppKindExtensions.TryParseCliString(kindArg, out var k))
                {
                    await Console.Error.WriteLineAsync($"Unknown kind '{kindArg}'. Valid: {string.Join(", ", Enum.GetValues<AppKind>().Select(v => v.ToCliString()))}");
                    return 1;
                }

                kind = k;
            }

            LiveProgressRenderer.RenderClear();

            var options = new PipelineOptions
            {
                ScopeKind = kind,
                ShowAll = allArg || kind is not null,
                DryRun = dryRunArg,
                PinPackage = pinArg,
                UnpinPackage = unpinArg
            };

            var result = await orchestrator.InvokeAsync(options, cancellationToken);

            var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
            _ = await SelfUpdateChecker.CheckForUpdateAsync(httpClientFactory, cancellationToken);

            return result;
        });
    }

    private static int HandleInstallToShell()
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

    /// <summary>Prints the release changelog (the GitHub release body) under a dim header, when present.</summary>
    private static void PrintChangelog(string? changelog)
    {
        if (string.IsNullOrWhiteSpace(changelog))
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine(AnsiStyle.Bold("What's new:"));
        foreach (var line in changelog.ReplaceLineEndings("\n").Trim('\n').Split('\n'))
        {
            Console.WriteLine(AnsiStyle.Dim($"  {line}"));
        }

        Console.WriteLine();
    }
}