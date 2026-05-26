using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Dotnet;

/// <summary>
/// Discovers all .NET SDKs installed on the system via <c>dotnet --list-sdks</c>.
/// Each installed SDK version is emitted as a separate <see cref="AppKind.Packages"/> entry.
/// </summary>
public sealed class DotnetScanner(IProcessRunner runner, ProjectManifestFinder finder, ILogger<DotnetScanner> logger)
    : IScanner
{
    private string? _executablePath;

    public string Name => "Dotnet";

    /// <inheritdoc/>
    public string DisplayName => ".NET";

    public OS SupportedOS => OS.MacOS | OS.Windows;

    public bool IsAvailable()
    {
        _executablePath = ScannerHelper.FindExecutable("dotnet");
        return _executablePath is not null;
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var sdk in EnumerateSdks(cancellationToken))
        {
            yield return sdk;
        }

        await foreach (var runtime in EnumerateRuntimes(cancellationToken))
        {
            yield return runtime;
        }

        await foreach (var tool in EnumerateGlobalTools(cancellationToken))
        {
            yield return tool;
        }

        await foreach (var local in EnumerateLocalTools(cancellationToken))
        {
            yield return local;
        }

        await foreach (var project in EnumerateProjects(cancellationToken))
        {
            yield return project;
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateRuntimes([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(_executablePath!, "--list-runtimes", cancellationToken);
        if (result.Success)
        {
            foreach (var line in Lines(result.StandardOutput))
            {
                var app = ParseRuntimeLine(line);
                if (app is not null)
                {
                    yield return app;
                }
            }
        }
        else
        {
            logger.LogWarning("'dotnet --list-runtimes' failed: {Err}", result.StandardError.Trim());
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateSdks([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Output: "8.0.300 [/usr/local/share/dotnet/sdk]"
        var result = await runner.RunAsync(_executablePath!, "--list-sdks", cancellationToken);
        if (result.Success)
        {
            foreach (var line in Lines(result.StandardOutput))
            {
                var app = ParseSdkLine(line);
                if (app is not null)
                {
                    yield return app;
                }
            }
        }
        else
        {
            logger.LogWarning("'dotnet --list-sdks' failed: {Err}", result.StandardError.Trim());
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateGlobalTools([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(_executablePath!, "tool list -g", cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning("'dotnet tool list -g' failed: {Err}", result.StandardError.Trim());
            yield break;
        }

        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Skip the two header lines:
        //   "Package Id   Version   Commands"
        //   "------…"
        var dataLines = lines.SkipWhile(l => l.StartsWith("Package", StringComparison.OrdinalIgnoreCase) ||
                                             l.StartsWith("------", StringComparison.Ordinal));

        foreach (var line in dataLines)
        {
            var app = ParseGlobalToolsLine(line);
            if (app is not null)
            {
                yield return app;
            }
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateLocalTools([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var manifestPath in finder.FindAsync("dotnet-tools.json", cancellationToken))
        {
            await foreach (var app in ParseLocalToolManifestAsync(manifestPath, cancellationToken))
            {
                yield return app;
            }
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateProjects([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var pattern in new[] { "*.csproj", "*.fsproj", "Directory.Packages.props" })
        {
            await foreach (var manifestPath in finder.FindAsync(pattern, cancellationToken))
            {
                await foreach (var app in ParseProjectManifestAsync(manifestPath, cancellationToken))
                {
                    yield return app;
                }
            }
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> ParseLocalToolManifestAsync(string manifestPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read tool manifest: {Path}", manifestPath);
            yield break;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Failed to parse tool manifest JSON: {Path}", manifestPath);
            yield break;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("tools", out var tools))
            {
                yield break;
            }

            foreach (var entry in tools.EnumerateObject())
            {
                var packageId = entry.Name;
                string? version = null;

                if (entry.Value.TryGetProperty("version", out var verProp))
                {
                    version = verProp.GetString();
                }

                yield return new DiscoveredApp(
                    packageId,
                    new AppIdentifier(Name, DisplayName, "Local Tool"),
                    AppKind.Libraries,
                    version,
                    ProjectFile: manifestPath,
                    SuggestedMethod: UpdateMethod.PackageRegistry,
                    SuggestedMethodDetail: packageId);
            }
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> ParseProjectManifestAsync(string manifestPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string xml;
        try
        {
            xml = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read manifest: {Path}", manifestPath);
            yield break;
        }

        XmlDocument doc;
        try
        {
            doc = new XmlDocument();
            doc.LoadXml(xml);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to parse XML: {Path}", manifestPath);
            yield break;
        }

        // Match both <PackageReference> (project files) and <PackageVersion> (Central Package Management)
        var nodes = doc.SelectNodes("//*[local-name()='PackageReference' or local-name()='PackageVersion']");
        if (nodes is null)
        {
            yield break;
        }

        foreach (XmlNode node in nodes)
        {
            var include = node.Attributes?["Include"]?.Value?.Trim();
            var version = node.Attributes?["Version"]?.Value?.Trim()
                          ?? node.SelectSingleNode("*[local-name()='Version']")?.InnerText?.Trim();

            if (string.IsNullOrWhiteSpace(include)) continue;

            yield return new DiscoveredApp(
                include,
                new AppIdentifier("NuGet", "NuGet", "Package"),
                AppKind.Libraries,
                string.IsNullOrWhiteSpace(version) ? null : version,
                ProjectFile: manifestPath,
                SuggestedMethod: UpdateMethod.PackageRegistry,
                SuggestedMethodDetail: include);
        }
    }

    private DiscoveredApp? ParseSdkLine(string line)
    {
        // "6.0.136 [/usr/local/share/dotnet/sdk]"
        var spaceIdx = line.IndexOf(' ');
        if (spaceIdx < 0)
        {
            return null;
        }

        var version = line[..spaceIdx];
        var basePath = ExtractBracketPath(line);

        return new DiscoveredApp(
            $".NET SDK {MajorMinor(version)}",
            new AppIdentifier(Name, DisplayName, "SDK"),
            AppKind.Packages,
            version,
            basePath is not null ? Path.Combine(basePath, version) : null,
            SuggestedMethod: UpdateMethod.Sdk);
    }

    private DiscoveredApp? ParseRuntimeLine(string line)
    {
        // "Microsoft.NETCore.App 8.0.5 [/usr/local/share/dotnet/shared/Microsoft.NETCore.App]"
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var runtimeName = parts[0];
        var version = parts[1];
        var basePath = ExtractBracketPath(line);

        return new DiscoveredApp(
            $"{runtimeName} {MajorMinor(version)}",
            new AppIdentifier("DotnetRuntime", DisplayName, "Runtime"),
            AppKind.Packages,
            version,
            basePath is not null ? Path.Combine(basePath, version) : null,
            SuggestedMethod: UpdateMethod.Sdk,
            SuggestedMethodDetail: runtimeName);
    }

    /// <summary>
    /// Line format (variable-width columns, multiple spaces as separator):
    ///   "dotnet-ef                            8.0.4        dotnet-ef"
    /// </summary>
    private DiscoveredApp? ParseGlobalToolsLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var packageId = parts[0];
        var version = parts[1];

        return new DiscoveredApp(
            packageId,
            new AppIdentifier(Name, DisplayName, "Global Tool"),
            AppKind.Packages,
            version,
            SuggestedMethod: UpdateMethod.PackageRegistry,
            SuggestedMethodDetail: packageId);
    }

    private static string? ExtractBracketPath(string line)
    {
        var open = line.LastIndexOf('[');
        var close = line.LastIndexOf(']');
        if (open < 0 || close <= open)
        {
            return null;
        }

        return line[(open + 1)..close];
    }

    /// <summary>
    /// Returns the <c>major.minor</c> segment of a version string so that different
    /// installed SDK generations (e.g. 6.0 and 10.0) get unique names and are not
    /// collapsed by the name-based deduplication in <c>--show-all</c>.
    /// </summary>
    private static string MajorMinor(string version)
    {
        var firstDot = version.IndexOf('.');
        if (firstDot < 0)
        {
            return version;
        }

        var secondDot = version.IndexOf('.', firstDot + 1);
        return secondDot > 0 ? version[..secondDot] : version;
    }

    private static IEnumerable<string> Lines(string output)
    {
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l));
    }
}