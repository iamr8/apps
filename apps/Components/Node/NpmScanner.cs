using System.Runtime.CompilerServices;
using System.Text.Json;

using apps.Infrastructure;
using apps.Scanners;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Node;

/// <summary>
/// Discovers globally installed npm packages via <c>npm list -g --depth=0 --json</c>.
/// Each entry is emitted as <see cref="AppKind.Package"/>.
/// </summary>
public sealed class NpmScanner(IProcessRunner runner, ProjectManifestFinder finder, ILogger<NpmScanner> logger)
    : IScanner
{
    private string? _executablePath;

    public string Name => "npm";

    /// <inheritdoc/>
    public string DisplayName => "npm";

    public OS SupportedOS => OS.MacOS | OS.Windows;

    /// <inheritdoc/>
    public string? GetSourceQualifier(AppKind kind) => kind == AppKind.Packages ? "Package" : null;

    public bool IsAvailable()
    {
        _executablePath = ScannerHelper.FindExecutable("npm");
        return _executablePath is not null;
    }

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var global in EnumerateGlobalPackages(cancellationToken))
        {
            yield return global;
        }

        await foreach (var project in EnumerateProjectPackages(cancellationToken))
        {
            yield return project;
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateProjectPackages([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var manifestPath in finder.FindAsync("package.json", cancellationToken))
        {
            await foreach (var app in ParseProjectManifestAsync(manifestPath, cancellationToken))
            {
                yield return app;
            }
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> EnumerateGlobalPackages([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(_executablePath!, "list -g --depth=0 --json", cancellationToken);

        // npm exits with a non-zero code when it encounters peer-dep warnings even
        // when real output was produced; use stdout if it exists regardless of exit code.
        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            logger.LogWarning("'npm list -g' produced no output. Err: {Err}", result.StandardError.Trim());
            yield break;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(result.StandardOutput);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse 'npm list -g' JSON output");
            yield break;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("dependencies", out var deps))
            {
                yield break;
            }

            foreach (var entry in deps.EnumerateObject())
            {
                var packageName = entry.Name;
                string? version = null;

                if (entry.Value.TryGetProperty("version", out var verProp))
                {
                    version = verProp.GetString();
                }

                yield return new DiscoveredApp(
                    packageName,
                    new AppIdentifier(Name, DisplayName, "Global Package"),
                    AppKind.Packages,
                    version,
                    SuggestedMethod: UpdateMethod.PackageRegistry,
                    SuggestedMethodDetail: packageName);
            }
        }
    }

    private async IAsyncEnumerable<DiscoveredApp> ParseProjectManifestAsync(string manifestPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Cannot read {Path}", manifestPath);
            yield break;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Cannot parse {Path}", manifestPath);
            yield break;
        }

        using (doc)
        {
            // Emit from both regular and dev dependencies
            foreach (var sectionName in new[] { "dependencies", "devDependencies" })
            {
                if (!doc.RootElement.TryGetProperty(sectionName, out var section))
                {
                    continue;
                }

                foreach (var dep in section.EnumerateObject())
                {
                    var name = dep.Name;
                    var rawVer = dep.Value.GetString() ?? "";

                    // Normalise version specifier: strip leading ^~>=<
                    var version = rawVer.TrimStart('^', '~', '=', '>', '<', ' ');

                    yield return new DiscoveredApp(
                        name,
                        new AppIdentifier(Name, DisplayName, "Package"),
                        AppKind.Libraries,
                        string.IsNullOrWhiteSpace(version) ? null : version,
                        ProjectFile: manifestPath,
                        SuggestedMethod: UpdateMethod.PackageRegistry,
                        SuggestedMethodDetail: name);
                }
            }
        }
    }
}