# apps

A macOS CLI tool that discovers every installed application, SDK, runtime, developer tool, and library on your Mac and checks each one for available updates. All state is in-memory -- every run re-scans and re-checks from scratch.

The tool is **non-destructive**: it reports what is outdated but never performs updates itself.


![Build Status](https://img.shields.io/github/actions/workflow/status/iamr8/apps/ci.yml?branch=main&style=flat-square&label=build)
![Security](https://img.shields.io/github/actions/workflow/status/iamr8/apps/security.yml?branch=main&style=flat-square&label=security)
![Latest Release](https://img.shields.io/github/v/release/iamr8/apps?style=flat-square)
![License](https://img.shields.io/github/license/iamr8/apps?style=flat-square)
![Platform](https://img.shields.io/badge/platform-macOS-blue?style=flat-square)
![.NET](https://img.shields.io/badge/.NET-10-purple?style=flat-square)
![Commits](https://img.shields.io/github/commit-activity/m/iamr8/apps?style=flat-square&label=commits)
![Last Commit](https://img.shields.io/github/last-commit/iamr8/apps?style=flat-square)
![Language](https://img.shields.io/github/languages/top/iamr8/apps?style=flat-square)

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

## Architecture

The tool uses a four-stage concurrent pipeline:

1. **Discovery** — all scanners run in parallel, streaming results through a bounded channel.
2. **Method Resolution** — apps without a suggested update method are matched against Homebrew casks/formulas and Chocolatey package catalogs. Includes catalog lookups and fuzzy search for unresolved GUI apps.
3. **Update Check** — apps are grouped by update method; all groups run concurrently, results stream to a live renderer.
4. **Security Audit** — all auditable packages are batch-queried against OSV.dev for known CVEs, then enriched with patched-version info from the GitHub Advisory Database.

## Requirements

- macOS 13 or later
- .NET 10 SDK (for building from source)
- Homebrew (detected at `/opt/homebrew` or `/usr/local`)
- Optional: `mas` CLI for App Store lookups
- Optional: `GITHUB_TOKEN` environment variable for higher GitHub API rate limits (5000 req/hr vs 60 req/hr)

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

## License

This project is licensed under the [Creative Commons Attribution-NonCommercial 4.0 International License](LICENSE).

You are free to use, share, and adapt this software for non-commercial purposes only.

