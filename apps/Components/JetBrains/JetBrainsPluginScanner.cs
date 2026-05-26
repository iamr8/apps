using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Xml;

using apps.Models;
using apps.Scanners;

using Microsoft.Extensions.Logging;

namespace apps.Components.JetBrains;

/// <summary>
/// Discovers JetBrains IDE plugins installed at
/// <c>~/Library/Application Support/JetBrains/{Product}{Version}/plugins/</c>.
/// Handles both extracted-directory plugins (<c>META-INF/plugin.xml</c> on disk)
/// and JAR-format plugins (<c>lib/*.jar</c> containing <c>META-INF/plugin.xml</c>).
/// One <see cref="AppKind.Extension"/> entry is emitted per unique plugin ID.
/// </summary>
public sealed class JetBrainsPluginScanner(ILogger<JetBrainsPluginScanner> logger)
    : IScanner
{
    private string[] _executablePaths;

    public string Name => "JetBrains";

    /// <inheritdoc/>
    public string DisplayName => "JetBrains";

    public OS SupportedOS => OS.MacOS | OS.Windows;

    /// <inheritdoc/>
    public string? GetSourceQualifier(AppKind kind) => kind == AppKind.Extension ? "Plugin" : null;

    public bool IsAvailable()
    {
        var root = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "JetBrains")
            : OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "JetBrains")
                : null;
        if (root is null)
        {
            return false;
        }

        if (!Directory.Exists(root))
        {
            return false;
        }

        try
        {
            _executablePaths = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly);
            return _executablePaths.Length > 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cannot list JetBrains product directories in {Root}", root);
            return false;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Track seen plugin IDs across all product dirs to avoid duplicates
        // (same plugin installed in Rider 2024.1 and 2024.3 should appear once).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var productDir in _executablePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pluginsDir = Path.Combine(productDir, "plugins");
            if (!Directory.Exists(pluginsDir))
            {
                continue;
            }

            string[] pluginDirs;
            try
            {
                pluginDirs = Directory.GetDirectories(pluginsDir, "*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var pluginDir in pluginDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DiscoveredApp? app = null;
                try
                {
                    app = await TryReadPluginAsync(pluginDir, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to read plugin at {Path}", pluginDir);
                }

                if (app is null || !seen.Add(app.SuggestedMethodDetail ?? app.Name))
                {
                    continue;
                }

                yield return app;
            }
        }
    }

    /// <summary>
    /// Attempts to read plugin metadata from a plugin directory.
    /// Tries <c>META-INF/plugin.xml</c> directly first, then scans <c>lib/*.jar</c>.
    /// </summary>
    private async Task<DiscoveredApp?> TryReadPluginAsync(string pluginDir, CancellationToken cancellationToken)
    {
        // Case 1: extracted plugin — META-INF/plugin.xml on disk
        var xmlPath = Path.Combine(pluginDir, "META-INF", "plugin.xml");
        if (File.Exists(xmlPath))
        {
            return await ParsePluginXmlAsync(xmlPath, xmlPath, cancellationToken);
        }

        // Case 2: JAR-format plugin — lib/*.jar with embedded META-INF/plugin.xml
        var libDir = Path.Combine(pluginDir, "lib");
        if (!Directory.Exists(libDir))
        {
            return null;
        }

        foreach (var jar in Directory.EnumerateFiles(libDir, "*.jar"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var app = TryReadPluginFromJar(jar);
            if (app is not null)
            {
                return app;
            }
        }

        return null;
    }

    private DiscoveredApp? TryReadPluginFromJar(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var entry = zip.GetEntry("META-INF/plugin.xml");
            if (entry is null)
            {
                return null;
            }

            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var xml = reader.ReadToEnd();
            return ParsePluginXmlString(xml, jarPath);
        }
        catch
        {
            return null;
        }
    }

    private async Task<DiscoveredApp?> ParsePluginXmlAsync(string xmlPath, string sourcePath, CancellationToken ct)
    {
        var xml = await File.ReadAllTextAsync(xmlPath, ct);
        return ParsePluginXmlString(xml, sourcePath);
    }

    private DiscoveredApp? ParsePluginXmlString(string xml, string sourcePath)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        var root = doc.DocumentElement;
        if (root is null)
        {
            return null;
        }

        var id = root.SelectSingleNode("id")?.InnerText?.Trim();
        var name = root.SelectSingleNode("name")?.InnerText?.Trim();
        var version = root.SelectSingleNode("version")?.InnerText?.Trim();

        var displayId = id ?? name;
        if (string.IsNullOrWhiteSpace(displayId))
        {
            return null;
        }

        return new DiscoveredApp(
            name ?? displayId,
            new AppIdentifier(Name, DisplayName, "Plugin"),
            AppKind.Extension,
            string.IsNullOrWhiteSpace(version) ? null : version,
            sourcePath,
            SuggestedMethod: UpdateMethod.Specialised,
            SuggestedMethodDetail: displayId);
    }
}