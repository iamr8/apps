# apps

A macOS CLI tool that discovers every installed application, SDK, runtime, developer tool, and library on your Mac and
checks each one for available updates. All state is in-memory -- every run re-scans and re-checks from scratch.

The tool is **non-destructive**: it reports what is outdated but never performs updates itself.

![Build Status](https://img.shields.io/github/actions/workflow/status/iamr8/apps/ci.yml?branch=main&style=flat-square&label=build)
![Security](https://img.shields.io/github/actions/workflow/status/iamr8/apps/security.yml?branch=main&style=flat-square&label=security)
![Latest Release](https://img.shields.io/github/v/release/iamr8/apps?style=flat-square)
![Last Commit](https://img.shields.io/github/last-commit/iamr8/apps?style=flat-square)
[![Build and Release](https://github.com/iamr8/apps/actions/workflows/release.yml/badge.svg)](https://github.com/iamr8/apps/actions/workflows/release.yml)

## Features

- Scans GUI applications from `/Applications` and `~/Applications`
- Discovers Homebrew formulas and casks, App Store apps
- Checks developer SDKs and runtimes: .NET, Node.js, Go, Xcode
- Inspects globally installed tools: dotnet global tools, npm -g packages, Go binaries
- Detects IDE extensions: VS Code, JetBrains plugins
- Examines Docker images for digest changes
- Identifies Electron apps and resolves their GitHub or generic update feeds
- Parses Sparkle appcasts for indie macOS apps
- Reports macOS Software Update items
- Streams results live as checks complete

## Architecture

The tool uses a three-stage concurrent pipeline:

1. **Discovery** — all scanners run in parallel, streaming results through a bounded channel. Update-source matching (
   e.g. Homebrew cask/formula resolution) happens inside each scanner.
2. **Update Check** — discovered apps are grouped back by their owning scanner; all scanners check concurrently, and
   results stream to a live renderer as each completes.
3. **Security Audit** — all auditable packages are batch-queried against OSV.dev for known CVEs, then enriched with
   patched-version info from the GitHub Advisory Database.

## Requirements

- macOS 13 or later

## Usage

```
apps                    # scan + check for updates (show only outdated)
apps --all | -a         # show all apps (outdated + up-to-date)
apps --kind | -k <kind> # show all apps of a specific kind
apps --dry-run | -d     # scan only -- discover apps without checking for updates
apps --pin | -p <name>  # pin a package at its current version (suppresses updates)
apps --unpin <name>     # remove a pin from a package
apps --install          # install "apps" to /usr/local/bin so it can be run from anywhere
apps --upgrade          # check if a newer version of apps is available
```

### Kind Filter

| Value     | What it shows                                                       |
|-----------|---------------------------------------------------------------------|
| `app`     | GUI .app bundles (including Electron apps)                          |
| `package` | Globally installed tools, runtimes, Homebrew formulas/casks, Docker |
| `service` | Background daemons (LaunchAgents/Daemons, Login Items)              |
| `ext`     | IDE extensions and editor plugins (VS Code, JetBrains)              |

## Supported Components

| Component | What it covers                                                                                                                      | 
|-----------|-------------------------------------------------------------------------------------------------------------------------------------|
| Audit     | CVE scanning via OSV.dev + GitHub Advisory Database                                                                                 | 
| Chrome    | Chrome extensions                                                                                                                   | 
| Docker    | Docker images (local digest vs Hub)                                                                                                 | 
| Dotnet    | .NET SDKs, runtimes, NuGet global tools                                                                                             | 
| Go        | Go binaries and tools in GOPATH/bin                                                                                                 | 
| JetBrains | JetBrains IDE plugins (IDEA, Rider, WebStorm, etc.)                                                                                 | 
| MacOs     | GUI .app bundles, Homebrew formulas/casks, App Store apps, Sparkle & Electron apps, macOS Software Update, Xcode, Safari extensions | 
| Node      | Node.js, npm global packages                                                                                                        | 
| VsCode    | VS Code extensions (marketplace)                                                                                                    | 

## Installation

### Build from Source

- .NET 10 SDK (for building from source)

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

