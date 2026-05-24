# apps — High-Level Design

> Binary name: **`apps`** · Status: **Draft v0.5** · Last updated: 2026-05-24

> **Rules, conventions, coding instructions, and obligations are maintained in [CLAUDE.md](../CLAUDE.md).**
> This document focuses on discovery sources, update-check logic, architecture, and performance.

---

## 1. Goals

| #  | Goal                                                                                       |
|----|--------------------------------------------------------------------------------------------|
| G1 | Discover **every** installed application, runtime, developer tool, and package on a Mac.   |
| G2 | Determine the best update channel for each discovered app, persist it, and reuse it.       |
| G3 | Report which apps are out-of-date in a fast, readable way.                                 |
| G4 | Be **non-destructive** — report what is outdated; never perform updates itself.            |
| G5 | Be **extensible** — adding a new component requires only a new folder under `Components/` and one line in `ComponentRegistration`. |

---

## 2. Discovery — What We Scan

### 2.1 GUI Applications

| Source                    | Mechanism               | Notes                                                                      |
|---------------------------|-------------------------|----------------------------------------------------------------------------|
| `/Applications`           | Directory walk, depth 1 | Primary location                                                           |
| `~/Applications`          | Directory walk, depth 1 | User-installed                                                             |
| `/System/Applications`    | Directory walk, depth 1 | System apps — tagged `AppKind.SystemApp`, never shown in output or checked |
| `/Applications/Utilities` | Directory walk, depth 1 | Utilities sub-folder                                                       |

For each `.app` bundle, read `Contents/Info.plist` and extract:

- `CFBundleDisplayName` / `CFBundleName` — display name
- `CFBundleShortVersionString` — installed version
- `CFBundleIdentifier` — bundle ID (used for App Store matching)
- `SUFeedURL` — Sparkle appcast URL (present in ~40% of indie apps)
- `SUPublicEDKey` / `SUPublicDSAKeyFile` — Sparkle 2 signing keys (existence confirms Sparkle)

Apple / OS system apps — bundles whose `CFBundleIdentifier` starts with `com.apple.`, or any bundle
under `/System/Applications` — are tagged `AppKind.SystemApp` with `UpdateMethod.None`.
They never appear in the scan-progress output, are excluded from `update --show-all` table output,
and are never subjected to update checks (the OS manages them via Software Update).

#### PWA / Browser-Hosted Web App Detection

Apps whose `CFBundleIdentifier` matches a known browser-generated pattern are tagged
`UpdateMethod.SelfUpdate`:

| Bundle-ID prefix            | Source                        |
|-----------------------------|-------------------------------|
| `com.apple.Safari.WebApp.*` | Safari web apps (macOS 14+)   |
| `com.google.Chrome.app.*`   | Google Chrome web apps / PWAs |
| `com.microsoft.edgeapp.*`   | Microsoft Edge web apps       |

These apps are shown in the `update --show-all` table with method `SelfUpdate` and status `↺`.
No external update check is performed — the host browser handles the update lifecycle.

Electron apps are user-installed GUI apps and are therefore tagged `Kind = AppKind.App`,
exactly like any other `.app` bundle discovered by `ApplicationsScanner`. The distinction is
purely in their **update method** (`UpdateMethod.Electron`), not their kind.

`ElectronScanner` is a specialisation pass that runs alongside `ApplicationsScanner`. It walks
the same application directories, identifies Electron bundles by the presence of
`Contents/Frameworks/Electron Framework.framework`, and reads
`Contents/Resources/app-update.yml` to resolve the update channel:

| `app-update.yml` field | `provider: github` | `provider: generic`          |
|------------------------|--------------------|------------------------------|
| `owner`                | GitHub org / user  | —                            |
| `repo`                 | GitHub repository  | —                            |
| `url`                  | —                  | Base URL for the update feed |

Because `app-update.yml` is simple flat YAML, it is parsed line-by-line without a YAML library
(AOT-safe; no reflection). Detected Electron apps are emitted as `AppKind.App` with
`SuggestedMethod = UpdateMethod.Electron` and `SuggestedMethodDetail` encoded as:

- `"github:{owner}/{repo}"` — for GitHub-hosted Electron releases
- `"generic:{url}"` — for self-hosted generic feeds (`{url}/latest-mac.yml`)

Because the method set by `ElectronScanner` is stored with `COALESCE(existing, incoming)` semantics for `update_method`,
the Electron
method is never overwritten on subsequent runs where `ApplicationsScanner` re-scans the same bundle without a suggested
method.

`ElectronScanner` only emits an app if `app-update.yml` is present and parseable. Electron apps
without a valid feed file are left for `ApplicationsScanner` to handle (Sparkle → GitHub
heuristics apply as normal).

### 2.2 Package Managers

| Scanner           | Shell Commands                                         | What It Finds                              |
|-------------------|--------------------------------------------------------|--------------------------------------------|
| `HomebrewScanner` | `brew list --versions` + `brew list --cask --versions` | Formulas and casks with installed versions |
| `AppStoreScanner` | `mas list`                                             | App Store apps with Apple ID & version     |
| `MacPortsScanner` | `port installed`                                       | MacPorts ports (optional)                  |
| `ChocoScanner`    | `choco list`                                           | Chocolatey packages (optional)             |

### 2.3 Developer SDKs & Runtimes

| Scanner              | Detection                  | Shell Command                                      | Kind    |
|----------------------|----------------------------|----------------------------------------------------|---------|
| `DotnetScanner`      | `dotnet` on PATH           | `dotnet --list-sdks`, `dotnet --list-runtimes`     | devtool |
| `NodeScanner`        | `node` on PATH or `~/.nvm` | `node --version`; `nvm list` if nvm present        | devtool |
| `GoScanner`          | `go` on PATH               | `go version`; scan `$(go env GOPATH)/bin`          | devtool |
| `XcodeScanner`       | `xcodebuild` on PATH       | `xcodebuild -version`; `xcrun simctl runtime list` | devtool |
| `MacOsUpdateScanner` | always available           | `softwareupdate --list --all`                      | devtool |

### 2.4 Extensions & Plugins

IDE extensions and browser extensions are discovered separately; their updates are checked against their own marketplace
or host-app APIs.

| Scanner                  | Detection                                                         | Commands / Data Source                                                                   | Kind |
|--------------------------|-------------------------------------------------------------------|------------------------------------------------------------------------------------------|------|
| `VsCodeExtScanner`       | `code` on PATH                                                    | `code --list-extensions --show-versions`                                                 | ext  |
| `JetBrainsPluginScanner` | `~/Library/Application Support/JetBrains/` exists                 | reads `META-INF/plugin.xml` (or embedded in `lib/*.jar`) per plugin directory            | ext  |
| `SafariExtScanner`       | `.appex` plug-in bundles inside any `.app` in the app directories | reads `NSExtensionPointIdentifier` from each plug-in's `Info.plist`                      | ext  |
| `ChromeExtScanner`       | `~/Library/Application Support/Google/Chrome` or `Chrome Canary`  | reads `Extensions/{id}/{version}/manifest.json` for each profile; deduplicates by ext ID | ext  |

> `JetBrainsPluginScanner` enumerates all installed JetBrains IDE products (IDEA, Rider, WebStorm, …)
> and for each one reads plugin metadata from
> `~/Library/Application Support/JetBrains/{product}{version}/plugins/*/META-INF/plugin.xml`.

> `SafariExtScanner` considers an extension as App Store–managed (`UpdateMethod.AppStore`) when the
> parent `.app` bundle contains a `Contents/_MASReceipt` directory; otherwise `UpdateMethod.SelfUpdate`.

> `ChromeExtScanner` uses `UpdateMethod.SelfUpdate` for all extensions — Chrome updates extensions
> silently via the CRX update protocol. Extensions with synthetic names (`__MSG_*`) are skipped.

### 2.5 Development Packages

Development packages are split into two tiers:

- **Global tools** — installed machine-wide via a package manager CLI; always scanned.
- **Project dependencies** — installed inside a project folder; opt-in via `update --include-project-deps`.

#### 2.5.1 Global / User-scope Package Tools

| Scanner                   | Detection                                  | Commands / Data Source                                                          | Kind    |
|---------------------------|--------------------------------------------|---------------------------------------------------------------------------------|---------|
| `NugetGlobalToolsScanner` | `dotnet` on PATH                           | `dotnet tool list -g`                                                           | package |
| `NugetLocalToolsScanner`  | `.config/dotnet-tools.json` in `~` subtree | `dotnet tool list` per manifest (opt-in)                                        | package |
| `NpmGlobalScanner`        | `npm` on PATH                              | `npm list -g --depth=0 --json`                                                  | package |
| `GoToolsScanner`          | `go` on PATH                               | binaries in `$(go env GOPATH)/bin`; `go version -m <binary>` for module version | package |

#### 2.5.2 Project-level Dependencies (opt-in, `--include-project-deps`)

Manifest files discovered anywhere under `~/` (respects `.gitignore`):

| Ecosystem       | Manifest Files                                     | Scanner               |
|-----------------|----------------------------------------------------|-----------------------|
| .NET / NuGet    | `*.csproj`, `*.fsproj`, `Directory.Packages.props` | `NugetProjectScanner` |
| Go              | `go.mod`                                           | `GoModScanner`        |
| Node.js         | `package.json` (excluding `node_modules/`)         | `NpmProjectScanner`   |
| Swift (SPM)     | `Package.swift`                                    | `SwiftPackageScanner` |
| C / C++ (vcpkg) | `vcpkg.json`                                       | `VcpkgScanner`        |

> **Deduplication rule:** if a package is already tracked as a global tool, project-level entries for the same name +
> version are merged rather than duplicated.

### 2.6 Docker Images

| Scanner              | Detection        | Commands / Data Source        | Kind    |
|----------------------|------------------|-------------------------------|---------|
| `DockerImageScanner` | `docker` on PATH | `docker images --format json` | package |

Returns one `DiscoveredApp` per unique `{repository}:{tag}` pair.
`installed_version` stores the local image digest; `update_method_detail` stores the full image reference.

### 2.7 Additional Heuristic Sources

- **`/usr/local/bin` & `/opt/homebrew/bin`** — diff against known Homebrew formulas; standalone binaries are tracked.
- **LaunchAgents / LaunchDaemons** — `~/Library/LaunchAgents`, `/Library/LaunchAgents` — find third-party background
  services.
- **Login Items** — `sfltool dumpbtm` (macOS 13+) — services that run at login.
- **Xcode / Simulators** — `xcodebuild -version`, `xcrun simctl runtime list`.

---

## 3. Update Checking — Priority Chain

For each app the system walks the chain from highest to lowest priority.
As soon as a method **succeeds** (confirms it can check the app), the result is used
and lower-priority methods are **skipped** for that app.

```
Priority 1 ── App Store (mas)
              └─ requires CFBundleIdentifier matching a MAS record OR entry in `mas list`
Priority 2 ── Homebrew Cask
              └─ match by cask name or CFBundleIdentifier in cask JSON
Priority 3 ── Homebrew Formula
              └─ match by formula name
Priority 4 ── Sparkle
              └─ SUFeedURL present in Info.plist → fetch appcast XML
Priority 5 ── Electron (electron-updater)
              └─ app-update.yml present in Contents/Resources → GitHub Releases or generic feed
Priority 6 ── GitHub Releases
              └─ detect repo via Info.plist metadata, executable strings, or cask source URL
Priority 7 ── MacPorts
              └─ match by port name
Priority 8 ── Chocolatey
              └─ match by choco package name; fallback for apps not found in Homebrew
Priority 9 ── Package Registry (NuGet · npm · Go module proxy)
              └─ used for dev packages whose scanner sets Kind = Package | Dep
Priority 10 ── Specialised Checkers
              └─ Docker Hub · VS Code Marketplace · JetBrains Plugin Repository · macOS Software Update
Priority 11 ── SDK-specific tools
              └─ dotnet sdk check · rustup check (for runtime/SDK-level items)
Priority 12 ── Unresolved (None)
              └─ tracked but no update mechanism found; flagged for manual review
Priority 13 ── SelfUpdate
              └─ PWA / browser-hosted web app; update managed by the host browser — no check performed
```

### 3.1 App Store Checker

- Runs `mas outdated` once; batches all App Store apps in a single subprocess call.

### 3.2 Homebrew Checker

- Runs `brew outdated --json=v2` once; parses JSON for formulas and casks simultaneously.

### 3.3 Sparkle Checker

- Fetches appcast XML from `SUFeedURL`.
- Parses `<enclosure sparkle:version="…">` and compares with installed version.
- Respects `sparkle:minimumSystemVersion` to avoid flagging macOS-incompatible updates.
- Handles both Sparkle 1 (RSS) and Sparkle 2 (same XML schema) appcasts.

### 3.4 Electron Checker (`ElectronChecker`)

Handles `UpdateMethod.Electron`; reads `app.UpdateMethodDetail` to determine the provider.

#### GitHub provider (`"github:{owner}/{repo}"`)

- Calls `GET https://api.github.com/repos/{owner}/{repo}/releases/latest`
- Parses `tag_name` (e.g. `"v2.1.0"`) and strips the leading `v` before comparing.
- Reuses the named `"github"` `HttpClient` from `IHttpClientFactory` so it benefits from
  the same `GITHUB_TOKEN` header, `SemaphoreSlim(8)` concurrency cap, and Retry-After
  back-off as `GitHubReleasesChecker`.
- JSON deserialized via a source-generated `JsonSerializerContext` (AOT-safe).

#### Generic provider (`"generic:{url}"`)

- Fetches `{url}/latest-mac.yml` (falls back to `{url}/latest.yml` on 404).
- Parses the `version:` line from the YAML file (line-by-line; no YAML library needed).
- Uses the wildcard `SemaphoreSlim(4)` slot from `RateLimitedHttpHandler`.

#### Rate limits

| Provider | In-flight cap         | Strategy                 |
|----------|-----------------------|--------------------------|
| github   | 8 w/ token; 2 without | shared with GitHub slot  |
| generic  | 4                     | wildcard `SemaphoreSlim` |

### 3.5 GitHub Releases Checker

- Repo detection heuristics (in order):
    1. `SUFeedURL` host is `github.com`
    2. Info.plist key `GitHubRepo` or `RepositoryURL`
    3. Homebrew cask formula `url` field points to a GitHub release
    4. Binary `strings` scan for `github.com/{org}/{repo}` pattern (opt-in, slow)
- Calls `GET https://api.github.com/repos/{owner}/{repo}/releases/latest`
- **Rate limits:** unauthenticated = 60 req/hr; set `GITHUB_TOKEN` env var for 5 000 req/hr.

### 3.6 Package Registry Checkers

| Checker                | Endpoint                                                            | Rate Limit                                                          | Notes                                                                 |
|------------------------|---------------------------------------------------------------------|---------------------------------------------------------------------|-----------------------------------------------------------------------|
| `NugetRegistryChecker` | `https://api.nuget.org/v3/registration5-gz-semver2/{id}/index.json` | No published limit; CDN-cached via Fastly — safe at ~200 req/min    | Use `Accept-Encoding: gzip`; responses are CDN-served instantly       |
| `NpmRegistryChecker`   | `https://registry.npmjs.org/{name}/latest`                          | No hard limit; Cloudflare-fronted — effectively unlimited for reads | Use `Accept: application/vnd.npm.install-v1+json` for minimal payload |
| `GoModProxyChecker`    | `https://proxy.golang.org/{module}/@latest`                         | No published limit; Google-cached — effectively unlimited           | Returns `{"Version":"v…","Time":"…"}`; responses are always cached    |

> **Shared infrastructure:** all three checkers share `RateLimitedHttpClient` which enforces
> configurable per-host concurrency (default 8) and backs off on any `429` / `Retry-After` response.
> Requests are fanned out concurrently up to the concurrency cap, then drained before the next batch.

### 3.7 Specialised Checkers

#### 3.7.1 Docker Hub Checker (`DockerHubChecker`)

- **API:** `GET https://hub.docker.com/v2/repositories/{namespace}/{repo}/tags?ordering=last_updated&page_size=10`
- **Auth:** anonymous token from
  `https://auth.docker.io/token?service=registry.docker.io&scope=repository:{name}:pull`  
  Optional `DOCKER_USERNAME` + `DOCKER_PASSWORD` env vars for authenticated requests.
- **Rate limits:**
    - Tag listing API — no documented limit; much looser than pull limits.
    - Pull-based rate limit (unauthenticated): 100 per 6 hr/IP; authenticated free: 200 per 6 hr.
    - We only request tags metadata, not actual pulls, so pull limits do **not** apply.
- **Strategy:** compare local image digest against the digest of the same tag on Hub; flag if different.

#### 3.7.2 VS Code Extension Checker (`VsCodeExtChecker`)

- **API:** `POST https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery?api-version=7.2-preview.1`
- **Auth:** none required for reads.
- **Rate limits:** not officially documented; Microsoft throttles by IP.
  Batch extensions: one POST can carry up to **100 extension IDs** in a single request, dramatically reducing call
  count.
- **Strategy:** parse `code --list-extensions --show-versions`; batch all IDs in one or
  a few POST requests; compare `versions[0].version` against installed version.

#### 3.7.3 JetBrains Plugin Checker (`JetBrainsPluginChecker`)

- **API:** `GET https://plugins.jetbrains.com/api/plugins/{pluginId}/updates?channel=&size=1`
- **Auth:** none required.
- **Rate limits:** not officially published; conservative limit of **4 req/s** per host is safe.
  Parallelise at 4 concurrent; `RateLimitedHttpClient` enforces this automatically.
- **Plugin ID source:** read `<id>` from each plugin's `META-INF/plugin.xml`; numeric or string IDs both work.
- **Strategy:** compare `version` from API response against installed version.

#### 3.7.4 macOS Software Update Checker (`MacOsUpdateChecker`)

- **Method:** subprocess — `softwareupdate --list --all 2>&1`
- **No rate limiting** (local Apple CDN query; not counted against any API quota).
- **Strategy:** parse stdout for `* Label:` lines (recommended updates) and `** Label:` (critically important).
  Emits one `UpdateCheckResult` per listed item; sets `update_available = true` on all hits.
- Results are tagged `Kind = DevTool` and `UpdateMethod = Sdk`; surfaced under `apps list --kind devtool`.

### 3.8 SDK Checkers

| Checker               | Method                                   | Kind    |
|-----------------------|------------------------------------------|---------|
| `DotnetUpdateChecker` | `dotnet sdk check` — parses stdout table | devtool |

---

## 4. Architecture

```
Program.cs  (DI root, System.CommandLine setup)

Commands/
  UpdateCommand ──► UpdateOrchestrator
                      ScanOrchestrator → MethodResolverOrchestrator → CheckOrchestrator → LiveProgressRenderer.RenderTable

Components/            vertical slices — one folder per component (scanner + checker + JSON + registration)
  Dotnet/       DotnetScanner, DotnetReleasesChecker, NugetGlobalToolsScanner,
                NugetLocalToolsScanner, NugetProjectScanner, NugetRegistryChecker
  Node/         NodeScanner, NpmGlobalScanner, NpmProjectScanner, NpmRegistryChecker
  Go/           GoScanner, GoToolsScanner, GoModScanner, GoModProxyChecker
  Homebrew/     HomebrewScanner, HomebrewCaskChecker, HomebrewFormulaChecker
  AppStore/     AppStoreScanner, AppStoreChecker
  MacPorts/     MacPortsScanner, MacPortsChecker
  Chocolatey/   ChocoScanner, ChocoChecker
  Docker/       DockerImageScanner, DockerHubChecker
  VsCode/       VsCodeExtScanner, VsCodeExtChecker
  JetBrains/    JetBrainsPluginScanner, JetBrainsPluginChecker
  GitHub/       GitHubReleasesChecker
  Sparkle/      SparkleChecker
  Electron/     ElectronScanner, ElectronChecker
  MacOs/        ApplicationsScanner, MacOsUpdateScanner, MacOsUpdateChecker,
                SafariExtScanner, ChromeExtScanner, XcodeScanner
  Swift/        SwiftPackageScanner
  Vcpkg/        VcpkgScanner

Scanners/         shared contracts (IScanner, IProjectLevelScanner, ScannerHelper)
Checkers/         shared contract  (IUpdateChecker)

Orchestration/
  ScanOrchestrator          -- stage 1: runs all scanners concurrently
  MethodResolverOrchestrator -- stage 1.5: resolves NULL methods via Homebrew → Choco fallback
  CheckOrchestrator         -- stage 2: runs all checkers concurrently


Models/
  DiscoveredApp    (Name, Version, Path, Kind, Scanner, BundleId?, ProjectFile?)
  UpdateCheckResult, AppRecord, CheckHistory
  AppKind          (enum: App | SystemApp | Packages | Libraries | Dep | Service | Extension)
  UpdateMethod     (enum: AppStore | HomebrewCask | HomebrewFormula | Sparkle |
                          Electron | GitHub | MacPorts | Chocolatey | PackageRegistry | Sdk | None)

Infrastructure/
  ProcessRunner          (shell exec abstraction, mockable)
  PlistReader            (parse Info.plist)
  RateLimitedHttpClient  (per-host concurrency + Retry-After support)
  HttpClientFactory      (named clients: GitHub, NuGet, npm, GoProxy, DockerHub, VSMarketplace, JetBrains)
  VersionComparer        (SemVer + date-based + 4-tuple .NET version fallback)
  ProjectManifestFinder  (walk ~ for known manifest file names, respect .gitignore)
  LiveProgressRenderer   (streaming per-app status lines to terminal, ANSI-aware;
                          filters out up-to-date results unless --show-all is set)
```

---

## 5. CLI Reference

The binary is invoked directly (no subcommand).

> App Kind values and the full update-method priority chain are documented in [CLAUDE.md](../CLAUDE.md).

### 5.1 `apps` — scan + check + table

```
apps [options]

  --all, -a                 Show all apps (outdated + up-to-date)
  --kind, -k <kind>         Scope to one type: app | package | lib | dep | service | ext
  --dry-run, -d             Scan only — show discovered apps without checking for updates
  --pin, -p <name>          Pin a package at its current version (suppresses update notifications)
  --unpin <name>            Remove a pin from a package
```

## 6. Performance Architecture

---

### 6.2 Stage 2 - Check Pipeline

Apps are grouped by `UpdateMethod`. All groups start concurrently;
results stream to a shared `Channel<UpdateCheckResult>` as each check completes —
the live renderer prints each line immediately without waiting for the whole run.

```csharp
// CheckOrchestrator.cs
var resultChannel = Channel.CreateBounded<UpdateCheckResult>(256);

var checkGroups = apps
    .GroupBy(a => a.UpdateMethod)
    .Select(g => CheckGroupAsync(g.Key, [.. g], resultChannel.Writer, ct));

// All groups start simultaneously; channel completes when all are done
_ = Task.WhenAll(checkGroups)
        .ContinueWith(_ => resultChannel.Writer.Complete());

// Render every result as it arrives
await foreach (var result in resultChannel.Reader.ReadAllAsync(ct))
{
    _renderer.Render(result);
}

// ---

async Task CheckGroupAsync(UpdateMethod method, IReadOnlyList<AppRecord> apps, ChannelWriter<UpdateCheckResult> writer, CancellationToken cancellationToken)
{
    await foreach (var result in _checkerMap[method].CheckStreamAsync(apps, cancellationToken))
    {
        await writer.WriteAsync(result, cancellationToken);
    }
}
```

### 6.1 Stage 1 - Discovery Pipeline
> so individual results flow out as each HTTP response arrives rather than after the whole batch.

---

### 6.3 HTTP Client Architecture

One `SocketsHttpHandler` per named host, registered via `IHttpClientFactory`.
The factory owns handler lifetime — callers never dispose handlers directly.

```csharp
// Program.cs (DI registration helper)
void AddCheckerClient(string name, string baseUrl, int maxConn,
                      Action<HttpClient>? headers = null)
{
    services.AddHttpClient(name, c => {
        c.BaseAddress = new Uri(baseUrl);
        headers?.Invoke(c);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        MaxConnectionsPerServer        = maxConn,
        PooledConnectionLifetime       = TimeSpan.FromMinutes(2),  // rotate to refresh DNS
        PooledConnectionIdleTimeout    = TimeSpan.FromSeconds(90), // reclaim idle sockets
        EnableMultipleHttp2Connections = true,
        AutomaticDecompression         = DecompressionMethods.GZip | DecompressionMethods.Brotli
    });
}

AddCheckerClient("nuget",            "https://api.nuget.org",               maxConn: 8);
AddCheckerClient("npm",              "https://registry.npmjs.org",          maxConn: 16,
    c => c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.npm.install-v1+json"));
AddCheckerClient("goproxy",          "https://proxy.golang.org",            maxConn: 16);
AddCheckerClient("github",           "https://api.github.com",              maxConn: 8);
AddCheckerClient("electron-generic", "https://",  /* per-request base URL */ maxConn: 4);
AddCheckerClient("sparkle",          "https://",  /* variable per URL */     maxConn: 8);
AddCheckerClient("dockerhub",        "https://hub.docker.com",              maxConn: 4);
### 6.2 Stage 2 - Check Pipeline
AddCheckerClient("jetbrains",        "https://plugins.jetbrains.com",       maxConn: 4);
```

| Handler property                 | Value         | Reason                                                         |
|----------------------------------|---------------|----------------------------------------------------------------|
| `PooledConnectionLifetime`       | 2 min         | Rotates sockets so DNS changes are picked up promptly          |
| `PooledConnectionIdleTimeout`    | 90 s          | Reclaims idle sockets; avoids `ECONNRESET` on stale keep-alive |
| `EnableMultipleHttp2Connections` | true          | More parallel H/2 streams per host                             |
| `AutomaticDecompression`         | GZip + Brotli | NuGet and npm compress responses; free throughput gain         |

---

### 6.4 Per-host Rate Limiting

Built on .NET 7+ `System.Threading.RateLimiting` — no Polly required.

```csharp
// Infrastructure/RateLimitedHttpClient.cs

// Concurrency limiters: cap parallel in-flight requests per host
private static readonly FrozenDictionary<string, SemaphoreSlim> _slots =
    new Dictionary<string, SemaphoreSlim>
    {
        ["api.nuget.org"]                = new(8,  8),
        ["registry.npmjs.org"]           = new(16, 16),
        ["proxy.golang.org"]             = new(16, 16),
        ["api.github.com"]               = new(8,  8),  // lower to 2 when no token
        ["hub.docker.com"]               = new(4,  4),
        ["plugins.jetbrains.com"]        = new(4,  4),
        ["marketplace.visualstudio.com"] = new(2,  2),  // batch: only 1-2 calls anyway
        ["*"]                            = new(4,  4),  // fallback for Sparkle etc.
    }.ToFrozenDictionary();

// Token-bucket for time-windowed hosts (JetBrains: safe at 4 req/s)
private static readonly TokenBucketRateLimiter _jetbrainsLimiter = new(
    new TokenBucketRateLimiterOptions
    {
        TokenLimit           = 4,
        ReplenishmentPeriod  = TimeSpan.FromSeconds(1),
### 6.3 HTTP Client Architecture
        AutoReplenishment    = true,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit           = 64
    });

public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken cancellationToken)
{
    var host = req.RequestUri!.Host;
    var sem  = _slots.GetValueOrDefault(host) ?? _slots["*"];

    // Token-bucket gate for time-sensitive hosts
    if (host == "plugins.jetbrains.com")
    {
        using var lease = await _jetbrainsLimiter.AcquireAsync(permitCount: 1, cancellationToken);
        if (!lease.IsAcquired)
        {
            throw new OperationCanceledException("JetBrains rate-limit queue full.");
        }
    }

    await sem.WaitAsync(ct);
    try
    {
        var response = await _inner.SendAsync(req, cancellationToken);

        // Honour Retry-After on 429 / 503 with a single automatic retry
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(10);
            await Task.Delay(delay, cancellationToken);
            return await SendAsync(req, cancellationToken);
        }
        return response;
    }
    finally
    {
        sem.Release();
    }
}
```

---

### 6.5 Concurrency Budget Summary
### 6.4 Per-host Rate Limiting
| Checker            | In-flight cap         | Rate strategy                           | Effective throughput               |
|--------------------|-----------------------|-----------------------------------------|------------------------------------|
| App Store (`mas`)  | 1 subprocess          | n/a                                     | 1 call total (batched)             |
| Homebrew           | 1 subprocess          | n/a                                     | 1 call total (batched)             |
| macOS SW Update    | 1 subprocess          | n/a                                     | 1 call total                       |
| Electron (github)  | 8 w/ token; 2 without | shared with GitHub `SemaphoreSlim`      | 5 000 req/hr with token            |
| Electron (generic) | 4                     | wildcard `SemaphoreSlim(4)`             | varies per feed host               |
| NuGet              | 8                     | `SemaphoreSlim(8)`                      | ~200 req/min (CDN-served)          |
| npm                | 16                    | `SemaphoreSlim(16)`                     | effectively unlimited (Cloudflare) |
| Go Proxy           | 16                    | `SemaphoreSlim(16)`                     | effectively unlimited (Google CDN) |
| GitHub             | 8 w/ token; 2 without | `SemaphoreSlim`                         | 5 000 req/hr with token            |
| Sparkle            | 8                     | `SemaphoreSlim(8)`                      | varies per feed host               |
| Docker Hub         | 4                     | `SemaphoreSlim(4)`                      | metadata only; no pull quota       |
| VS Code            | 2                     | `SemaphoreSlim(2)`                      | 1-2 calls total (100 IDs/req)      |
| JetBrains          | 4                     | `SemaphoreSlim(4)` + `TokenBucket(4/s)` | 4 req/s sustained                  |

---

### 6.6 Subprocess Concurrency

Different tool subprocesses run concurrently; a `SemaphoreSlim(6)` prevents spawning too many:

```csharp
// Infrastructure/ProcessRunner.cs
private static readonly SemaphoreSlim _cap = new(6, 6);

public async Task<ProcessResult> RunAsync(string exe, string args, CancellationToken ct)
{
    await _cap.WaitAsync(ct);
    try
    {
        using var proc = new Process { StartInfo = BuildStartInfo(exe, args) };
        proc.Start();
        // stdout + stderr MUST be read concurrently with WaitForExitAsync
        // to avoid deadlock when the child fills its pipe buffer
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return new ProcessResult(proc.ExitCode, await stdoutTask, await stderrTask);
    }
    finally { _cap.Release(); }
}
```

---

### 6.7 Expected End-to-End Timings

Typical developer Mac, ~200 tracked items, GitHub token configured:

| Phase                                 | Duration     | Bottleneck                         |
|---------------------------------------|--------------|------------------------------------|
| All scanners (parallel)               | 3-6 s        | `brew list` (~2-3 s)               |
| Homebrew + App Store check            | 1-2 s        | one subprocess each                |
| NuGet + npm + Go (8-16x parallel)     | 2-4 s        | RTT x ceil(N / concurrency)        |
| GitHub Releases (8x, with token)      | 3-8 s        | app count                          |
| Sparkle (8x concurrent)               | 2-5 s        | feed-server latency                |
| VS Code (1-2 batch POSTs)             | 0.5 s        | single round-trip                  |
| JetBrains (token-bucket 4/s)          | N/4 s        | deliberately throttled             |
| Docker (4x concurrent)                | 1-2 s        | image count                        |
| macOS Software Update (background)    | 5-15 s       | Apple CDN                          |
| **Total wall-clock (fully parallel)** | **~10-20 s** | `brew list` scan + GitHub dominate |

> `MacOsUpdateChecker` runs concurrently with all HTTP checkers; its 15 s worst case is
> completely hidden behind the rest of the pipeline.
