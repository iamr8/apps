using System.Diagnostics.CodeAnalysis;

namespace apps.Models;

/// <summary>
/// Discriminator for every discovered entry.
/// CLI-facing string values (used with --kind): app | sysapp | package | lib | dep | service | ext
/// </summary>
public enum AppKind
{
    /// <summary>User-installed GUI .app bundles from /Applications, ~/Applications.</summary>
    App,

    /// <summary>
    /// Apple / OS system applications (com.apple.* bundle ID or /System/Applications).
    /// These are tracked for inventory only — they cannot be updated independently of the OS.
    /// </summary>
    SystemApp,

    /// <summary>
    /// Tools, runtimes, and globally installed CLI packages:
    /// .NET SDK, Node.js, Go, Xcode, dotnet global tools, npm -g packages,
    /// Go GOPATH/bin binaries, Docker images, Homebrew formulas and casks, MacPorts ports.
    /// </summary>
    Packages,

    /// <summary>
    /// Project-level library dependencies declared in manifest files:
    /// NuGet packages (*.csproj), npm packages (package.json), Go modules (go.mod),
    /// Swift packages (Package.swift), vcpkg dependencies (vcpkg.json).
    /// </summary>
    Libraries,

    /// <summary>
    /// Miscellaneous or ambiguous dependencies not yet classified into a more specific kind.
    /// </summary>
    Dep,

    /// <summary>Background daemons in LaunchAgents/LaunchDaemons or Login Items.</summary>
    Service,

    /// <summary>
    /// IDE add-ons and editor plug-ins installed into a specific host application:
    /// VS Code extensions, JetBrains IDE plugins.
    /// </summary>
    Extension
}

public static class AppKindExtensions
{
    extension(AppKind kind)
    {
        /// <summary>Returns the CLI string representation of this kind (e.g. "app", "ext").</summary>
        public string ToCliString()
        {
            return kind switch
            {
                AppKind.App => "app",
                AppKind.SystemApp => "sysapp",
                AppKind.Packages => "package",
                AppKind.Libraries => "lib",
                AppKind.Dep => "dep",
                AppKind.Service => "service",
                AppKind.Extension => "ext",
                _ => kind.ToString().ToLowerInvariant()
            };
        }

        /// <summary>Returns the section header label shown above each kind group in the results table.</summary>
        public string ToGroupLabel()
        {
            return kind switch
            {
                AppKind.App => "Apps",
                AppKind.Extension => "Extensions",
                AppKind.Packages => "Developer Tools",
                AppKind.Libraries => "Libraries",
                AppKind.Dep => "Dependencies",
                AppKind.Service => "Services",
                _ => kind.ToString()
            };
        }
    }

    /// <summary>Parses a CLI string (e.g. "app", "ext") into an <see cref="AppKind"/>.</summary>
    public static bool TryParseCliString(string value, [NotNullWhen(true)] out AppKind? kind)
    {
        kind = value.ToLowerInvariant() switch
        {
            "app" => AppKind.App,
            "sysapp" => AppKind.SystemApp,
            "package" => AppKind.Packages,
            "lib" => AppKind.Libraries,
            "dep" => AppKind.Dep,
            "service" => AppKind.Service,
            "ext" => AppKind.Extension,
            _ => (AppKind)(-1)
        };
        return (int)kind >= 0;
    }
}