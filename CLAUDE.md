# apps — Claude Context

## Project Overview

`apps` is a macOS CLI tool (binary: **`apps`**) written in **.NET 10 (C#)** that discovers
every installed application, SDK, runtime, developer tool, and library on a Mac and checks each one
for available updates. All state is in-memory only — every run re-scans and re-checks from scratch.

> Design principles, discovery sources, update-check logic, pipeline architecture, and performance details are documented in [DESIGN.md](DESIGN.md).

## Tech Stack

| Layer              | Choice                                       |
|--------------------|----------------------------------------------|
| Language / Runtime | C# · .NET 10 (AOT-ready)                     |
| CLI framework      | `System.CommandLine`                         |
| HTTP               | `System.Net.Http.HttpClient` (typed clients) |
| JSON               | `System.Text.Json`                           |
| Testing            | xUnit                                        |

## NuGet Package Versions

The exact versions currently referenced in `apps.csproj`. Always consult this table before
using a package API to avoid calling features that do not exist in the pinned version.

| Package                                    | Version | Notes                                                |
|--------------------------------------------|---------|------------------------------------------------------|
| `Microsoft.Extensions.DependencyInjection` | 10.0.8  | DI container                                         |
| `Microsoft.Extensions.Http`                | 10.0.8  | `IHttpClientFactory` + named/typed clients           |
| `Microsoft.Extensions.Logging.Console`     | 10.0.8  | Console log provider wired into Serilog              |
| `Serilog`                                  | 4.3.1   | Core logging library                                 |
| `Serilog.Extensions.Logging`               | 10.0.0  | Bridge: `ILogger<T>` → Serilog pipeline              |
| `Serilog.Sinks.Console`                    | 6.1.1   | Terminal sink (ANSI-aware)                           |
| `Serilog.Sinks.File`                       | 7.0.0   | Rolling-file sink                                    |
| `System.CommandLine`                       | 2.0.8   | CLI verbs, options, and argument parsing             |
| `System.Threading.RateLimiting`            | 10.0.8  | `TokenBucketRateLimiter`, `SlidingWindowRateLimiter` |

## Repository Layout

```
apps/
├── CLAUDE.md                          ← this file
├── DESIGN.md                          ← high-level architecture & decisions
├── apps.slnx
└── apps/
    ├── apps.csproj
    ├── Program.cs                     ← CLI root / DI composition
    ├── Commands/                      ← root command configuration (options & action)
    ├── Scanners/                      ← shared IScanner / IProjectLevelScanner contracts
    ├── Checkers/                      ← shared IUpdateChecker contract
    ├── Models/                        ← shared DTOs
    ├── Infrastructure/                ← ProcessRunner, HttpClientFactory, etc.
    ├── Orchestration/                 ← pipeline coordination (scan → resolve → check)
    └── Components/                   ← vertical slices — one folder per component
        ├── ComponentRegistration.cs ← single entry point: AddAllComponents()
        ├── Dotnet/                    ← scanner, checker, NuGet tools, registration
        ├── Node/                      ← scanner, npm global/project, registry checker
        ├── Go/                        ← scanner, tools, go.mod, proxy checker
        ├── Homebrew/                  ← scanner, cask+formula checkers
        ├── AppStore/                  ← scanner, iTunes lookup checker
        ├── MacPorts/                  ← scanner, checker
        ├── Chocolatey/                ← scanner, checker
        ├── Docker/                    ← image scanner, Docker Hub checker
        ├── VsCode/                    ← extension scanner, marketplace checker
        ├── JetBrains/                 ← plugin scanner, plugin repo checker
        ├── GitHub/                    ← GitHub Releases checker
        ├── Sparkle/                   ← appcast checker
        ├── Electron/                  ← app-update.yml scanner, checker
        ├── MacOs/                     ← Applications scanner, SW Update, Safari, Chrome, Xcode
        ├── Swift/                     ← Package.swift scanner
        └── Vcpkg/                     ← vcpkg.json scanner
```

## Architecture & Key Conventions

- **Async everywhere** — all I/O is `async/await`; `Task<T>` return types.
- **Scanner interface** — every scanner implements `IScanner` and returns `IAsyncEnumerable<DiscoveredApp>`.
- **Checker interface** — every checker implements `IUpdateChecker`; checkers self-report which `UpdateMethod` they
  handle.
- **`Kind` discriminator** — every `DiscoveredApp` carries an `AppKind` value:
  `App | SystemApp | Packages | Libraries | Dep | Service | Extension`.
  CLI-facing string values (used with `--kind`): `app | package | lib | dep | service | ext`.
  `SystemApp` is excluded from `--kind` since system apps cannot be updated independently of the OS.
- **No caching** — every run re-scans and re-checks from scratch; all state is in-memory only.
- **Update-method priority** —
  `AppStore > Homebrew Cask > Homebrew Formula > Sparkle > Electron > GitHub > MacPorts > Chocolatey > PackageRegistry > SDK-specific`.
- **No duplicate work** — the update pipeline skips the homebrew pass for apps that already have a suggested method from
  their scanner.
- **Project deps are excluded** — project-manifest scanners (`NugetProjectScanner`, `GoModScanner`, etc.) are always
  skipped; only global/user-scope tools are scanned.
- **ProcessRunner** — all shell invocations go through `Infrastructure.ProcessRunner` so they can be mocked in tests.
- **RateLimitedHttpClient** — all registry HTTP calls go through a shared wrapper that respects `Retry-After` and
  enforces per-host concurrency limits.
- **Nullable enabled** — treat every compiler warning as an error; resolve all warnings before completing any change.
- **AOT-compatible** — all code must be AOT-friendly, performant, and memory-efficient. No reflection-based
  serialization, no `dynamic`, no `Assembly.Load` at runtime. Use source-generated `JsonSerializerContext` for all
  JSON, and prefer value types and spans over heap allocations in hot paths.
- **Extensible (Vertical Slices)** — each component lives in its own `Components/<Name>/` folder containing scanners,
  checkers, JSON models, and a `<Name>Registration.cs` extension method. Adding a new component (e.g. Rust) means
  creating a new folder, implementing the slice, and chaining its registration in `ComponentRegistration.AddAllComponents()`.
- **Non-destructive** — the tool reports what is outdated; it never performs updates itself.
- **Graceful shutdown** — `Console.CancelKeyPress` (Ctrl+C) and `PosixSignalRegistration` (SIGTERM) cancel the
  root `CancellationTokenSource`. The token propagates through all scanners, checkers, HTTP calls, and subprocesses
  so in-flight work stops cooperatively. Exit code 130 on SIGINT.
- **Two-stage concurrent pipeline:**
    - Stage 1 (Discovery): all scanners run in parallel, results flow through a bounded `Channel<DiscoveredApp>` (
      capacity 512, `FullMode = Wait`).
    - Stage 2 (Check): apps are grouped by `UpdateMethod`; all groups run concurrently, results stream through a
      `Channel<UpdateCheckResult>` to the live renderer.
- **Subprocess concurrency** — a `SemaphoreSlim(6)` in `ProcessRunner` caps the number of concurrent child processes.
- **Per-host HTTP rate limiting** — `RateLimitedHttpHandler` uses `SemaphoreSlim` per host and a
  `TokenBucketRateLimiter`
  for JetBrains (4 req/s). Backs off on any `429` / `Retry-After` response with a single automatic retry.
- **`SocketsHttpHandler` per named client** — `PooledConnectionLifetime = 2 min`, `PooledConnectionIdleTimeout = 90 s`,
  `EnableMultipleHttp2Connections = true`, `AutomaticDecompression = GZip | Brotli`.
- **Electron apps** — tagged `AppKind.App` with `UpdateMethod.Electron`; `app-update.yml` is parsed line-by-line
  (no YAML library) for AOT safety. Detail encoded as `"github:{owner}/{repo}"` or `"generic:{url}"`.
- **PWA / browser-hosted apps** — tagged `UpdateMethod.SelfUpdate`; no external check is performed.
- **System apps** — bundles whose `CFBundleIdentifier` starts with `com.apple.` or any bundle under
  `/System/Applications` — tagged `AppKind.SystemApp` with `UpdateMethod.None`. Never appear in output. Never checked.
- **Deduplication** — if a package is already tracked as a global tool, project-level entries for the same name +
  version are merged rather than duplicated.

## App Kind Reference

| Kind        | CLI value | What it covers                                                                                                                                                                    |
|-------------|-----------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `App`       | `app`     | GUI `.app` bundles from `/Applications`, `~/Applications` — including Electron apps and PWAs; macOS Software Update items                                                         |
| `Packages`  | `package` | Globally installed tools & runtimes: .NET SDK, Node.js, Go, Rust, dotnet global tools, npm -g, Go GOPATH/bin binaries, Docker images, Homebrew formulas and casks, MacPorts ports |
| `Libraries` | `lib`     | Project-level library dependencies from manifest files (`*.csproj`, `go.mod`, `package.json`, `Package.swift`, `vcpkg.json`) — **opt-in only**                                    |
| `Dep`       | `dep`     | Miscellaneous or ambiguous dependencies not yet classified into a more specific kind                                                                                              |
| `Service`   | `service` | Background daemons in `LaunchAgents` / `LaunchDaemons` or Login Items                                                                                                             |
| `Extension` | `ext`     | IDE add-ons and editor plug-ins: VS Code extensions, JetBrains IDE plugins                                                                                                        |

> `AppKind.SystemApp` is used internally but **excluded from all output** — system apps cannot be updated independently
> of the OS.

## Update-Method Priority Chain

```
Priority 1  — App Store        requires CFBundleIdentifier matching a MAS record or entry in `mas list`
Priority 2  — Homebrew Cask    match by cask name or CFBundleIdentifier
Priority 3  — Homebrew Formula match by formula name
Priority 4  — Sparkle          SUFeedURL present in Info.plist → fetch appcast XML
Priority 5  — Electron         app-update.yml in Contents/Resources → GitHub Releases or generic feed
Priority 6  — GitHub Releases  detect repo via Info.plist metadata, executable strings, or cask source URL
Priority 7  — MacPorts         match by port name
Priority 8  — Chocolatey       match by choco package name
Priority 9  — Package Registry NuGet · npm · Go module proxy
Priority 10 — Specialised      Docker Hub · VS Code Marketplace · JetBrains Plugin Repository · macOS Software Update
Priority 11 — SDK-specific     dotnet sdk check · rustup check
Priority 12 — Unresolved       tracked but no update mechanism found; flagged for manual review
Priority 13 — SelfUpdate       PWA / browser-hosted web app; update managed by the host browser
```

## Key Interfaces

```csharp
interface IScanner
{
    string Name { get; }
    bool IsAvailable();
    IAsyncEnumerable<DiscoveredApp> ScanAsync(CancellationToken cancellationToken);
}

interface IUpdateChecker
{
    UpdateMethod Method { get; }
    bool CanCheck(AppRecord app);
    Task<UpdateCheckResult> CheckAsync(AppRecord app, CancellationToken cancellationToken);
    Task<IReadOnlyList<UpdateCheckResult>> CheckBatchAsync(
        IReadOnlyList<AppRecord> apps, CancellationToken cancellationToken);
}
```

## CLI Usage

```
apps                        # scan + check for updates → display outdated apps
apps --all | -a             # show all apps (outdated + up-to-date)
apps --kind | -k <kind>    # show all apps of a specific kind
apps --dry-run | -d         # scan only — show discovered apps without checking for updates
apps --pin | -p <name>     # pin a package at its current version (suppresses update notifications)
apps --unpin <name>        # remove a pin from a package
```

- macOS 13+
- Homebrew installed at `/opt/homebrew` (Apple Silicon) or `/usr/local` (Intel); detected at runtime.
- `mas` CLI optionally installed for App Store lookups.
- `dotnet`, `node`, `python3`, `rustup`, `go`, `rbenv`, `nvm`, `sdkman`, etc. — all optional; scanners
  degrade gracefully when a tool is absent.

## Log File Location

`~/.local/share/apps/log/`

## Running Locally

```bash
cd apps
dotnet run
dotnet run -- -a
```

## Testing

```bash
dotnet test
```

## Coding Instructions

1. Always follow `.editorconfig` rules during implementation.
2. Don't write a comment for every line. Only add a comment when a line needs clarification or documents a non-obvious
   trade-off.
3. Avoid categorization comments (`// --- SOMETHING ---`). Place code in the right location instead of labeling
   sections.
4. When a class, interface, or record has no body, write it single-lined with a semicolon: use `;` instead of `{ }`.
5. Before concluding that any change is correct, build the project (`dotnet build`) and run it with
   `dotnet run -- -a` to verify the change is correctly applied and visible in the tool's output.
   You **must** execute both commands, capture their terminal output, and include the relevant
   portion of that output in your response — a build success message and the first visible lines of
   `-a` output. Never mark a task as complete without doing this.
6. Write C# XML doc comments (`///`) for all public methods and properties — short, clear, and straight to the point.
7. Don't pad code with extra spaces to align with surrounding lines. Write naturally, like a C# developer would.
8. Always use braces for all control flow blocks — no braceless `if`, `for`, `while`, etc.
9. Add empty lines inside methods where it aids readability — especially after a closing brace `}`.
10. Every piece of written code must strictly follow these instructions. Deviating from any rule requires explicit
    manual approval.
11. Any post-approval code change that violates the design or these instructions must be reflected back in the relevant
    documentation to keep it up to date.
12. Inside an async method, prefer `await using` over `using` when a type implements `IAsyncDisposable`.
13. Primary constructors are preferred over explicit constructors unless the body needs field validation or complex
    initialization.
14. Methods that never return (always throw) must be annotated with `[DoesNotReturn]`.
15. `TryParse`-style methods should annotate their out parameter with `[NotNullWhen(true)]` where applicable.
16. Use compiler and runtime attributes liberally: `[MethodImpl]`, `[SkipLocalsInit]`, `[DoesNotReturn]`,
    `[NotNullWhen]`, `[MemberNotNull]`, etc.
17. Local functions must be placed at the end of their parent method, after any `return` statement.
18. If a method or constructor has more than 3 parameters, write each parameter on its own line. Otherwise, keep them on
    a single line.
19. Prefer `ToArray()` over `ToList()` when the result is not going to be mutated.
20. When writing log messages, put the entire message template on one line and each argument on its own line.
21. Use the `Lock` class (introduced in .NET 9) instead of a plain `object` for `lock` targets.
22. `lock` statement bodies must always use braces, even for single statements.
23. Use `.ConfigureAwait(false)` on all `await` calls in library/infrastructure code; omit it only in command handlers
    that must stay on the original context.
24. Prefer a Vertical Slice architecture: group code by feature rather than by layer.
25. `[GeneratedRegex]` attributes must always specify
    `RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase` (add or remove `IgnoreCase` only
    when case-sensitivity is intentional and documented).
26. All code must be AOT-friendly, performant, and memory-efficient. No reflection-based serialization, no `dynamic`,
    no `Assembly.Load` at runtime. Use source-generated `JsonSerializerContext` for all JSON, and prefer value types
    and spans over heap allocations in hot paths.
27. Whenever JSON serialization or deserialization is needed, use a `[JsonSerializable]`-annotated
    `JsonSerializerContext` (source-generated). Never pass a plain `Type` to `JsonSerializer` at runtime.
28. Pass `CancellationToken` to every async method and propagate it through all downstream calls. When the app is
    stopped, all in-progress I/O and subprocess operations must stop — no fire-and-forget tasks that block shutdown.
29. No more than one blank line between any two adjacent members (field, property, method, etc.).
30. `private readonly` fields must always appear at the top of the class body, before the explicit constructor (if one
    is provided).
31. Properties must always appear after the explicit constructor — only when a primary constructor is not used.
32. Within a class: public methods first, then private non-static methods, then private static methods. Private static
    members are always last.
33. Never write single-line block bodies for control flow constructs. Opening and closing braces for `try`, `catch`,
    `if`, `else`, `for`, `while`, `using`, etc. must each occupy their own line.
34. All compiler warnings must be resolved before a change is considered complete. Never leave a warning unaddressed.
