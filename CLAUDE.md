# apps — Claude Context

## Project Overview

`apps` is a macOS CLI tool (binary: **`apps`**) written in **.NET 10 (C#)** that discovers
every installed application, SDK, runtime, developer tool, and library on a Mac and checks each one
for available updates. All state is in-memory only — every run re-scans and re-checks from scratch.

## Tech Stack

| Layer              | Choice                                       |
|--------------------|----------------------------------------------|
| Language / Runtime | C# · .NET 10 (AOT-ready)                     |
| CLI framework      | `System.CommandLine`                         |
| HTTP               | `System.Net.Http.HttpClient` (typed clients) |
| JSON               | `System.Text.Json`                           |

## Architecture & Key Conventions

- **Async everywhere** — all I/O is `async/await`; `Task<T>` return types.
- **Scanner interface** — every scanner implements `IScanner`: it discovers apps via `ScanAsync`
  (`IAsyncEnumerable<DiscoveredApp>`) and resolves their updates via `CheckAsync`. Discovery and update-checking are
  owned by the same slice; there is no separate checker contract.
- **`Kind` discriminator** — every `DiscoveredApp` carries an `AppKind` value:
  `App | Package | Service | Extension`.
  CLI-facing string values (used with `--kind`): `app | package | service | ext`.
- **No caching** — every run re-scans and re-checks from scratch; all state is in-memory only.
- **Update-source priority** — per app, the resolution order is
  `AppStore > Homebrew Cask > Homebrew Formula > Sparkle > Electron > GitHub > PackageRegistry > SDK-specific`.
- **No duplicate work** — the update pipeline skips the Homebrew pass for apps that have already resolved a version from
  their scanner.
- **Project deps are excluded** — only global/user-scope tools are scanned; project-manifest dependencies are not.
- **ProcessRunner** — all shell invocations go through `ProcessRunner` so they can be mocked in tests.
- **Rate-limited HTTP clients** — registry HTTP calls go through named clients created by `AddCheckerClient`, each
  wrapped in a Polly resilience pipeline that enforces a per-client concurrency limit and retries transient
  failures while honouring `Retry-After`.
- **Nullable enabled** — treat every compiler warning as an error; resolve all warnings before completing any change.
- **AOT-compatible** — all code must be AOT-friendly, performant, and memory-efficient. No reflection-based
  serialization, no `dynamic`, no `Assembly.Load` at runtime. Use source-generated `JsonSerializerContext` for all
  JSON, and prefer value types and spans over heap allocations in hot paths.
- **Extensible (Vertical Slices)** — each component lives in its own `Components/<Name>/` folder containing scanners,
  checkers, JSON models, and a `<Name>Registration.cs` extension method. Adding a new component (e.g. Rust) means
  creating a new folder, implementing the slice, and chaining its registration in
  `ComponentRegistration.AddAllComponents()`.
- **Non-destructive** — the tool reports what is outdated; it never performs updates itself.
- **Graceful shutdown** — `Console.CancelKeyPress` (Ctrl+C) and `PosixSignalRegistration` (SIGTERM) cancel the
  root `CancellationTokenSource`. The token propagates through all scanners, checkers, HTTP calls, and subprocesses
  so in-flight work stops cooperatively. Exit code 130 on SIGINT.
- **Three-stage concurrent pipeline:**
    - Stage 1 (Discovery): all scanners run in parallel (`ScanOrchestrator`), results flow through a bounded
      `Channel<DiscoveredApp>` (capacity 512). Update-source matching (e.g. Homebrew cask/formula resolution) happens
      inside each scanner's own `CheckAsync`, not as a separate stage.
    - Stage 2 (Update Check): `CheckOrchestrator` groups discovered apps back by their owning scanner, runs every
      scanner's `CheckAsync` concurrently, and streams results to the live renderer as each check completes.
    - Stage 3 (Security Audit): auditable packages are batch-queried against OSV.dev for known CVEs (`OsvAuditChecker`),
      then GHSA-prefixed results are enriched with patched-version info from the GitHub Advisory Database REST API.
- **Subprocess concurrency** — a `SemaphoreSlim(6)` in `ProcessRunner` caps the number of concurrent child processes.
- **Per-client HTTP rate limiting** — each named client built by `AddCheckerClient` carries a Polly `ConcurrencyLimiter`
  (per-client request gate) and retries transient `429` / `5xx` responses (up to 3 attempts) honouring `Retry-After`.
- **`SocketsHttpHandler` per named client** — `PooledConnectionLifetime = 2 min`, `PooledConnectionIdleTimeout = 90 s`,
  `EnableMultipleHttp2Connections = true`, `AutomaticDecompression = GZip | Brotli`.
- **Electron apps** — tagged `AppAttribute.ElectronApp`; `app-update.yml` is parsed line-by-line
  (no YAML library) for AOT safety. Detail encoded as `"github:{owner}/{repo}"` or `"generic:{url}"`.
- **PWA / browser-hosted apps** — tagged `AppAttribute.PwaApp`; no external check is performed.
- **System apps** — bundles under `/System/Applications` (or `com.apple.*` bundles) are skipped during discovery —
  never emitted, never tracked, never checked.
- **Deduplication** — if a package is already tracked as a global tool, duplicate entries for the same name +
  version are merged rather than duplicated.

## App Kind Reference

| Kind        | CLI value | What it covers                                                                                                                                              |
|-------------|-----------|-------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `App`       | `app`     | GUI `.app` bundles from `/Applications`, `~/Applications` — including Electron apps and PWAs; macOS Software Update items                                   |
| `Package`   | `package` | Globally installed tools & runtimes: .NET SDK, Node.js, Go, dotnet global tools, npm -g, Go GOPATH/bin binaries, Docker images, Homebrew formulas and casks |
| `Service`   | `service` | Background daemons in `LaunchAgents` / `LaunchDaemons` or Login Items                                                                                       |
| `Extension` | `ext`     | IDE add-ons and editor plug-ins: VS Code extensions, JetBrains IDE plugins                                                                                  |

## CLI Usage

```
apps                        # scan + check for updates → display outdated apps
apps --all | -a             # show all apps (outdated + up-to-date)
apps --kind | -k <kind>     # show all apps of a specific kind
apps --dry-run | -d         # scan only — show discovered apps without checking for updates
apps --pin | -p <name>      # pin a package at its current version (suppresses update notifications)
apps --unpin <name>         # remove a pin from a package
apps --install              # install "apps" to /usr/local/bin so it can be run from anywhere
apps --upgrade              # check if a newer version of apps is available
apps --version | -v         # show the current version of apps
```

## Log File Location

`~/.local/share/apps/log/`

## Running Locally

```bash
cd apps
dotnet run
dotnet run -- -a
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
35. Use the `gh` CLI anytime you need to verify something on GitHub — workflow runs, releases, tags, PRs, issues, or
    repository state. Take advantage of its commands and capabilities instead of guessing or relying on assumptions.

## Commit Convention

This project follows [Conventional Commits](https://www.conventionalcommits.org/). Every commit message uses the format:

```
<type>(<scope>): <short description>
```

| Type    | When to use                                                  |
|---------|--------------------------------------------------------------|
| `feat`  | A new feature or meaningful change to existing functionality |
| `fix`   | A bug fix                                                    |
| `docs`  | Documentation-only changes (README, CLAUDE.md)               |
| `ci`    | Changes to GitHub Actions workflows or CI configuration      |
| `test`  | Adding or updating tests                                     |
| `perf`  | Performance improvements with no functional change           |
| `chore` | Routine maintenance (dependency bumps managed by Dependabot) |

**Scopes** (optional, in parentheses):

| Scope  | Meaning                  |
|--------|--------------------------|
| `deps` | Dependency version bumps |

**Examples:**

```
feat: add Rust toolchain scanner and crates.io checker
fix: handle nil version in Sparkle appcast response
docs: update README badges — remove redundant tag
ci: add CI, SBOM generation, and license compliance workflows
ci(deps): bump actions/checkout from 4 to 6
chore(deps): bump Serilog from 4.3.0 to 4.3.1
```

**Rules:**

- Use lowercase for the entire subject line.
- No period at the end.
- Keep the subject under 72 characters.
- Use imperative mood ("add", "fix", "update" — not "added", "fixes", "updated").
- Every file change in a single commit must be directly related to the same logical change.
  Never bundle unrelated modifications into one commit.

## Versioning, Tagging & Releasing

This project uses [Semantic Versioning](https://semver.org/) (`MAJOR.MINOR.PATCH`).

### VERSION File

The single source of truth for the app version is the `VERSION` file at the repository root.
The `.csproj` reads from it at build time — there is no need to edit version numbers in XML.

```
VERSION          ← contains e.g. "1.2.0" (no "v" prefix, no trailing newline beyond one LF)
```

### How to Bump the Version

1. Edit the `VERSION` file with the new version number.
2. Commit: `git commit -am "chore: bump version to X.Y.Z"`
3. Push: `git push origin main`

> **⚠️ CRITICAL:** The VERSION file change must be in its own **single, dedicated commit** with
> exactly the message `chore: bump version to X.Y.Z`. Never combine it with other changes.
> This is required for the CI tagging pipeline to work correctly.

The CI pipeline (`.github/workflows/tag.yml`) detects the VERSION change, creates and pushes
`vX.Y.Z` tag automatically, which then triggers the release workflow
(`.github/workflows/release.yml`) to build AOT binaries and publish a GitHub Release.

### Tagging Rules

- Tags use a `v` prefix: `v1.0.0`, `v1.1.0`, `v2.0.0-beta.1`.
- Every tag must correspond exactly to the content of the `VERSION` file at that commit.
- Never move or delete a published tag.

### Creating a GitHub Release

1. Push the tag (see above).
2. On GitHub, go to **Releases → Draft a new release**.
3. Select the tag, write release notes summarising changes since the last release.
4. Attach the AOT-published binary (`publish/apps`) if available.
5. Publish.

The `--upgrade` flag in the CLI checks the latest GitHub Release tag against the embedded
version and notifies the user when a newer release is available.

### When to Increment

| Change type                             | Bump    |
|-----------------------------------------|---------|
| Breaking CLI interface or output format | `MAJOR` |
| New scanner, checker, or CLI option     | `MINOR` |
| Bug fix, performance tweak, docs update | `PATCH` |


