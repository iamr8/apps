# apps

A macOS CLI tool that discovers every installed application, SDK, runtime, developer tool, and library on your Mac and checks each one for available updates. All state is in-memory -- every run re-scans and re-checks from scratch.

The tool is **non-destructive**: it reports what is outdated but never performs updates itself.


![Commits](https://img.shields.io/github/commit-activity/m/iamr8/apps?style=flat-square&label=commits)
![Last Commit](https://img.shields.io/github/last-commit/iamr8/apps?style=flat-square)
![Language](https://img.shields.io/github/languages/top/iamr8/apps?style=flat-square)
![Build Status](https://img.shields.io/github/actions/workflow/status/iamr8/apps/ci.yml?branch=main&style=flat-square)
![Latest Release](https://img.shields.io/github/v/release/iamr8/apps?style=flat-square)
![Latest Tag](https://img.shields.io/github/v/tag/iamr8/apps?style=flat-square)

## Features

- Scans GUI applications from `/Applications` and `~/Applications`
- Discovers Homebrew formulas and casks, App Store apps, MacPorts ports
- Checks developer SDKs and runtimes: .NET, Node.js, Go, Xcode
- Inspects globally installed tools: dotnet global tools, npm -g packages, Go binaries
- Detects IDE extensions: VS Code, JetBrains plugins
- Examines Docker images for digest changes
- Identifies Electron apps and resolves their GitHub or generic update feeds
- Parses Sparkle appcasts for indie macOS apps
- Reports macOS Software Update items
- Streams results live as checks complete

## Requirements

- macOS 13 or later
- .NET 10 SDK (for building from source)
- Homebrew (detected at `/opt/homebrew` or `/usr/local`)
- Optional: `mas` CLI for App Store lookups
- Optional: `GITHUB_TOKEN` environment variable for higher GitHub API rate limits (5000 req/hr vs 60 req/hr)

## Installation

### Build from Source

```bash
git clone https://github.com/iamr8/apps.git
cd apps
dotnet publish apps/apps.csproj -c Release -r osx-arm64 -o publish
```

For Intel Macs, use `-r osx-x64` instead.

The self-contained, trimmed, AOT-compiled binary will be at `publish/apps`.

To make it available system-wide:

```bash
sudo cp publish/apps /usr/local/bin/apps
```

Now you can run `apps` from any directory.

### From GitHub Releases

Download the latest pre-built binary from [Releases](https://github.com/iamr8/apps/releases):

```bash
tar -xzf apps-osx-arm64.tar.gz
chmod +x apps
sudo mv apps /usr/local/bin/
```

## Usage

```
apps                    # scan + check for updates (show only outdated)
apps --all | -a         # show all apps (outdated + up-to-date)
apps --kind | -k <kind> # show all apps of a specific kind
apps --dry-run | -d     # scan only -- discover apps without checking for updates
apps --pin | -p <name>  # pin a package at its current version (suppresses updates)
apps --unpin <name>     # remove a pin from a package
```

### Kind Filter

| Value     | What it shows                                                          |
|-----------|------------------------------------------------------------------------|
| `app`     | GUI .app bundles (including Electron apps)                             |
| `package` | Globally installed tools, runtimes, Homebrew formulas/casks, Docker    |
| `lib`     | Project-level library dependencies (opt-in)                            |
| `dep`     | Miscellaneous dependencies                                             |
| `service` | Background daemons (LaunchAgents/Daemons, Login Items)                 |
| `ext`     | IDE extensions and editor plugins (VS Code, JetBrains)                 |

## Architecture

The tool uses a four-stage concurrent pipeline:

1. **Discovery** — all scanners run in parallel, streaming results through a bounded channel.
2. **Method Resolution** — apps without a suggested update method are matched against Homebrew casks/formulas and Chocolatey package catalogs. Includes catalog lookups and fuzzy search for unresolved GUI apps.
3. **Update Check** — apps are grouped by update method; all groups run concurrently, results stream to a live renderer.
4. **Security Audit** — all auditable packages are batch-queried against OSV.dev for known CVEs, then enriched with patched-version info from the GitHub Advisory Database.

Each phase shows a live progress bar with a real-time seconds counter.

Each component lives in its own vertical slice under `Components/`:

```
apps/
  Components/
    AppStore/       -- scanner + checker
    Chocolatey/     -- scanner + checker
    Docker/         -- image scanner + Docker Hub checker
    Dotnet/         -- SDK scanner, NuGet tools, registry checker
    Electron/       -- scanner + checker (GitHub/generic feeds)
    GitHub/         -- GitHub Releases checker (fallback)
    Go/             -- scanner, tools, go.mod, proxy checker
    Homebrew/       -- scanner + cask/formula checker
    JetBrains/      -- plugin scanner + plugin repo checker
    MacOs/          -- Applications scanner, SW Update, Safari/Chrome extensions, Xcode
    MacPorts/       -- scanner + checker
    Node/           -- scanner, npm global/project, registry checker
    Sparkle/        -- appcast checker
    Swift/          -- Package.swift scanner
    Vcpkg/          -- vcpkg.json scanner
    VsCode/         -- extension scanner + marketplace checker
```

## Supported Components

| Component   | What it covers                                                                                         | Scanner                         | Checker                        |
|-------------|--------------------------------------------------------------------------------------------------------|---------------------------------|--------------------------------|
| AppStore    | macOS App Store apps (via `mas` CLI)                                                                   | `AppStoreScanner`               | `AppStoreChecker`              |
| Audit       | CVE vulnerability scanning via OSV.dev + GitHub Advisory Database                              | --                              | `OsvAuditChecker`, `GitHubAdvisoryEnricher` |
| Chocolatey  | Chocolatey packages (Windows cross-check)                                                              | `ChocoScanner`                  | `ChocoChecker`                 |
| Docker      | Docker images (local digest vs Hub)                                                                    | `DockerImageScanner`            | `DockerHubChecker`             |
| Dotnet      | .NET SDKs, runtimes, NuGet global/local tools, project packages                                        | `DotnetScanner`, `NugetGlobalToolsScanner`, `NugetLocalToolsScanner`, `NugetProjectScanner`, `DotnetRuntimeScanner` | `DotnetReleasesChecker`, `NugetRegistryChecker` |
| Electron    | Electron apps with `app-update.yml` (GitHub or generic feed)                                           | `ElectronScanner`               | `ElectronChecker`              |
| GitHub      | Apps updatable via GitHub Releases API                                                                  | --                              | `GitHubReleasesChecker`        |
| Go          | Go binaries, tools in GOPATH/bin, go.mod dependencies                                                  | `GoScanner`, `GoToolsScanner`, `GoModScanner` | `GoModProxyChecker`  |
| Homebrew    | Homebrew formulas and casks                                                                            | `HomebrewScanner`               | `HomebrewChecker`              |
| JetBrains   | JetBrains IDE plugins (IDEA, Rider, WebStorm, etc.)                                                    | `JetBrainsPluginScanner`        | `JetBrainsPluginChecker`       |
| MacOs       | GUI .app bundles, macOS Software Update, Xcode, Safari extensions, Chrome extensions                   | `ApplicationsScanner`, `MacOsUpdateScanner`, `XcodeScanner`, `SafariExtScanner`, `ChromeExtScanner` | `MacOsUpdateChecker` |
| MacPorts    | MacPorts ports                                                                                         | `MacPortsScanner`               | `MacPortsChecker`              |
| Node        | Node.js, npm global packages, npm project packages                                                     | `NodeScanner`, `NpmGlobalScanner`, `NpmProjectScanner` | `NpmRegistryChecker` |
| Sparkle     | Indie macOS apps using Sparkle framework (appcast XML)                                                 | --                              | `SparkleChecker`               |
| Swift       | Swift Package Manager dependencies (Package.swift)                                                     | `SwiftPackageScanner`           | --                             |
| Vcpkg       | C/C++ vcpkg dependencies (vcpkg.json)                                                                  | `VcpkgScanner`                  | --                             |
| VsCode      | VS Code extensions (marketplace)                                                                       | `VsCodeExtScanner`              | `VsCodeExtChecker`             |

## Adding a New Component

To add support for a new ecosystem (e.g. Rust, Python/pip):

1. Create a folder under `apps/Components/YourComponent/`.

2. Implement a **scanner** that implements `IScanner`:

```csharp
public sealed class YourScanner(IProcessRunner processRunner) : IScanner
{
    public string Name => "YourComponent";

    public bool IsAvailable()
        => File.Exists("/path/to/tool");

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Discover installed items and yield them
        yield return new DiscoveredApp(
            Name: "example-package",
            Scanner: Name,
            Kind: AppKind.Packages,
            InstalledVersion: "1.0.0",
            SuggestedMethod: UpdateMethod.PackageRegistry,
            SuggestedMethodDetail: "example-package");
    }
}
```

3. Implement a **checker** that implements `IUpdateChecker`:

```csharp
public sealed class YourChecker(IHttpClientFactory httpClientFactory) : IUpdateChecker
{
    public UpdateMethod Method => UpdateMethod.PackageRegistry;
    public string DisplayName => "YourComponent Registry";
    public (string Label, string? Qualifier)? SourceOverride => ("YourComponent", null);

    public bool CanCheck(AppRecord app)
        => app is { UpdateMethod: UpdateMethod.PackageRegistry, Scanner: "YourComponent" };

    public async Task<UpdateCheckResult> CheckAsync(AppRecord app, CancellationToken cancellationToken)
    {
        // Fetch the latest version from your registry
        // Compare with app.InstalledVersion
        // Return the result
    }

    public async Task<IReadOnlyList<UpdateCheckResult>> CheckBatchAsync(
        IReadOnlyList<AppRecord> apps, CancellationToken cancellationToken)
    {
        return await Task.WhenAll(apps.Select(a => CheckAsync(a, cancellationToken)));
    }

    public async IAsyncEnumerable<UpdateCheckResult> CheckStreamAsync(
        IReadOnlyList<AppRecord> apps,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var task in Task.WhenEach(apps.Select(a => CheckAsync(a, cancellationToken))))
        {
            yield return await task;
        }
    }
}
```

4. Create a **registration** extension method:

```csharp
public static class YourComponentRegistration
{
    public static IServiceCollection AddYourComponent(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, YourScanner>();
        services.AddSingleton<IUpdateChecker, YourChecker>();
        return services;
    }
}
```

5. Chain it in `Components/ComponentRegistration.cs`:

```csharp
services.AddYourComponent();
```

### Key Rules

- All I/O must be `async/await` with `CancellationToken` propagated everywhere.
- Use `IProcessRunner` for shell commands (never spawn processes directly).
- Use `IHttpClientFactory` named clients for HTTP (never create raw `HttpClient`).
- All JSON must use source-generated `JsonSerializerContext` (AOT-safe).
- Scanners return `IAsyncEnumerable<DiscoveredApp>`.
- The tool must remain AOT-compatible: no reflection, no `dynamic`, no runtime assembly loading.

## Update Method Priority

When multiple update channels apply to the same app, the highest priority wins:

| Priority | Method           | Source                                        |
|----------|------------------|-----------------------------------------------|
| 1        | App Store        | `mas` CLI / bundle ID matching                |
| 2        | Homebrew Cask    | cask name or bundle ID                        |
| 3        | Homebrew Formula | formula name                                  |
| 4        | Sparkle          | SUFeedURL in Info.plist                       |
| 5        | Electron         | app-update.yml in .app bundle                 |
| 6        | GitHub Releases  | repo detection heuristics                     |
| 7        | MacPorts         | port name                                     |
| 8        | Chocolatey       | choco package name                            |
| 9        | Package Registry | NuGet, npm, Go module proxy                   |
| 10       | Specialised      | Docker Hub, VS Code, JetBrains, macOS SW Upd  |
| 11       | SDK              | dotnet sdk check, rustup check                |
| 12       | None             | No mechanism found                            |
| 13       | SelfUpdate       | PWA / browser-hosted (managed by host)        |

## Log Files

Logs are written to `~/.local/share/apps/log/`.

## Development

```bash
# Run in development
cd apps
dotnet run
dotnet run -- -a
dotnet run -- -k package

# Build release
dotnet build -c Release

# Run tests
dotnet test
```

## License

This project is licensed under the [Creative Commons Attribution-NonCommercial 4.0 International License](LICENSE).

You are free to use, share, and adapt this software for non-commercial purposes only.

