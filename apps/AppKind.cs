using System.Diagnostics.CodeAnalysis;

namespace apps;

/// <summary>
/// Discriminator for every discovered entry.
/// CLI-facing string values (used with --kind): app | sysapp | package | lib | dep | service | ext
/// </summary>
[Flags]
public enum AppKind : byte
{
    None = 0,

    /// <summary>User-installed GUI .app bundles from /Applications, ~/Applications.</summary>
    App = 1,

    Package = 2,

    DevTool = 4,

    /// <summary>Background daemons in LaunchAgents/LaunchDaemons or Login Items.</summary>
    Service = 8,

    /// <summary>
    /// IDE add-ons and editor plug-ins installed into a specific host application:
    /// VS Code extensions, JetBrains IDE plugins.
    /// </summary>
    Extension = 16
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
                AppKind.Package => "package",
                AppKind.DevTool => "dev",
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
                AppKind.Package => "Packages",
                AppKind.DevTool => "Dev Tools",
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
            "package" => AppKind.Package,
            "dev" => AppKind.DevTool,
            "service" => AppKind.Service,
            "ext" => AppKind.Extension,
            _ => AppKind.None
        };
        return (int)kind >= 0;
    }
}