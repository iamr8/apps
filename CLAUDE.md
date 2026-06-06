## Project Context

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
| Testing            | `TUnit` · `Microsoft.Testing.Platform`       |

## Architecture & Key Conventions

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

## CLI Usage

```
apps                        # scan + check for updates → display outdated apps
apps --all | -a             # show all apps (outdated + up-to-date)
apps --kind | -k <kind>     # show all apps of a specific kind
apps --dry-run | -d         # scan only — show discovered apps without checking for updates
apps --pin | -p <name>      # pin a package at its current version (suppresses update notifications)
apps --unpin <name>         # remove a pin from a package
apps --install              # install "apps" to /usr/local/bin so it can be run from anywhere
apps --upgrade | -u         # update "apps" to the latest version if a newer one is available
apps --version | -v         # show the current version of apps
```

## Log File Location

In case you missed an error message in the console, the full log with detailed error information is always available at:

`~/.local/share/apps/log/`

## Running Locally

```bash
cd apps
dotnet run
dotnet run -- -a
```

## Testing

```bash
dotnet test                                  # run the whole suite
dotnet test --project apps.Tests/apps.Tests.csproj   # run only the test project
```

Key setup details:

- **MTP opt-in** — `global.json` at the repo root sets `test.runner = "Microsoft.Testing.Platform"`.
  This is required for `dotnet test` to drive TUnit on the .NET 10 SDK; without it `dotnet test`
  fails with the legacy VSTest error.
- **Project reference** — the main project's `SelfContained` is gated on a `RuntimeIdentifier`
  being present (`Condition="'$(RuntimeIdentifier)' != ''"`), so a plain `dotnet build` (and the
  test-project reference) stays framework-dependent while `publish.sh` / `release.yml` (which pass
  `-r <RID>`) still produce self-contained AOT binaries. `InternalsVisibleTo("apps.Tests")` exposes
  `internal` helpers to the tests.
- **Test doubles** — hand-rolled fakes under `apps.Tests/Fakes/` (`FakeProcessRunner`,
  `StubHttpMessageHandler` + `StubHttpClientFactory`, `FakeScanner`). No mocking library.
- **Seams** — when discovery/parsing logic is entangled with the filesystem or PATH, extract the
  pure part into an `internal static` method and test that directly (e.g.
  `NodeScanner.ParseGlobalPackages` / `BuildRegistryPath`). Inject file paths via constructor where
  a type writes to a fixed location (e.g. `PinManager`'s internal path-taking constructor).
- **Assertions are awaited** — TUnit uses `await Assert.That(actual).IsEqualTo(expected)`.
  Tests run under the machine's real culture (not `InvariantGlobalization`), so they also catch
  locale-dependent parsing bugs — always parse/format with `CultureInfo.InvariantCulture`.

CI runs the suite on every push/PR via `.github/workflows/ci.yml` (`dotnet test`).

## Conventions & Guidelines

### Documentation

- Write C# XML doc comments (`///`) for all public methods, properties, and types - short, clear, and straight to the
  point.
- Don't write a comment for every line of the code. Only add a comment when a line needs clarification or documents a
  non-obvious trade-off according to the principle of "self-documenting code". If you find yourself writing a comment
  to
  explain what the code does, consider refactoring the code to clarify it instead of adding a comment.
  Comments should explain why something is done a certain way, not what the code is doing. If the code is clear and
  straightforward, it should not require comments to be understood. On the other hand, if the code is complex or
  contains non-obvious decisions, comments can be helpful to explain the rationale behind those decisions. Always aim
  for clarity and maintainability in your code and use comments judiciously to enhance understanding when necessary.
  Comments should not be verbal, redundant, or obvious — they should provide insight that the code alone does not
  convey in short and straightforward language.
- Avoid categorization comments (`// --- SOMETHING ---`). Place code in the right location instead of labeling
  sections.
- All C# XML doc comments must be the Microsoft-style doc.
- Use `<remarks>` only when there are some considerations, trade-offs, or non-obvious information that the reader should
  be aware of. If the method or code is
  straightforward and doesn't require additional context, it's better to omit the `<remarks>` section to keep the
  documentation concise and focused on the essential information.
- All comments and docs must be human-readable and concise with a plain language style, and free of typos and
  grammatical
  errors. They should be easy to understand and provide clear explanations without unnecessary complexity or jargon.

### Style

- Always follow `.editorconfig` rules during implementation.
- When a class, interface, or record has no body, write it single-lined with a semicolon: use `;` instead of `{ }`.
- Don't pad code with extra spaces to align with surrounding lines. Write naturally, like a C# developer would.
- Always use braces for all control flow blocks — no braceless `if`, `for`, `while`, `lock`, etc.
- Never write single-line block bodies for control flow constructs. Opening and closing braces for `try`, `catch`,
  `if`, `else`, `for`, `while`, `using`, etc. must each occupy their own line.
- Add empty lines inside methods where it aids readability — especially after a closing brace `}`.
- Primary constructors are preferred over explicit constructors unless the body needs field validation or complex
  initialization.
- If a method or constructor has more than 2 parameters, chop it to multiple lines with one parameter per line.
  Otherwise, keep them on a single line.
- Write the entire log method in a single line.
- No more than one blank line between any two adjacent members (field, property, method, etc.).
- In an implementation, the order of methods/members should be:
    - private readonly fields
    - explicit constructor (if the primary constructor is not used)
    - properties (if the primary constructor is not used)
    - public non-static methods
    - private non-static methods
    - public static methods
    - private static methods
- Don't change the order of existing members when modifying a class. If you need to add a new member, place it in the
  correct
  location according to the above rules, but don't rearrange existing members just for the sake of ordering.
- Don't change the existing portions of the codebase that are not directly related to your change. If you need to
  modify a method, class, or file,
  only change the specific lines that are necessary for your change. Avoid making unrelated formatting or style
  changes to existing code, as this can create noise in the commit history and make it harder to review and understand
  the actual changes being made.

### Language Features & APIs

- Inside an async method, prefer `await using` over `using` when a type implements `IAsyncDisposable`.
- If you have a method that may complete synchronously mostly but can also be awaited for a specific low-rate path
  consider using `ValueTask` which can improve performance by avoiding the overhead of allocating a `Task` object.
  However, if the method is expected to be awaited multiple times or if it does not complete
  synchronously, returning `Task` may be more appropriate to ensure correct behavior and avoid potential pitfalls with
  `ValueTask`. Always consider the specific use case and performance implications when choosing between `Task` and
  `ValueTask`.
- Methods that never return (always throw) must be annotated with `[DoesNotReturn]`.
- `TryParse`-style methods should annotate their out parameter with `[NotNullWhen(true)]` where applicable.
- Use compiler and runtime attributes liberally: `[MethodImpl]`, `[NotNullWhen]`, `[MemberNotNull]`, etc.
- Local functions must be placed at the end of their parent method, after any `return` statement.
- For all regular expressions that are used more than once, use the `[GeneratedRegex]` attribute to generate a static
  regex field.
- `[GeneratedRegex]` attributes must always specify `RegexOptions.Compiled | RegexOptions.CultureInvariant`.
- Prefer `ToArray()` over `ToList()` when the result is not going to be mutated.
- Use `.ConfigureAwait(false)` on all `await` calls in library/infrastructure code; omit it only in command handlers
  that must stay on the original context.
- Pass `CancellationToken` to every async method and propagate it through all downstream calls. When the app is
  stopped, all in-progress I/O and subprocess operations must stop — no fire-and-forget tasks that block shutdown.
- Security is non-negotiable. Always use secure APIs and practices when handling sensitive data, authentication,
  authorization, and
  any operations that could potentially expose vulnerabilities.
- When a type does not have any reference - but is called by other code via reflection, dynamic, or is instantiated in a
  way that static analysis cannot detect - decorate it with `[DynamicallyAccessedMembers]` and `[UsedImplicitly]`
  attributes.
- Use the `Lock` class instead of a plain `object` for `lock` targets.

### Verification

- Before concluding that any change is correct, build the project (`dotnet build`) and run it with
  `dotnet run -- -a` to verify the change is correctly applied and visible in the tool's output. Never mark a task as
  complete without doing this.
- Every piece of written code must strictly follow these instructions. Deviating from any rule requires explicit
  manual approval.
- Any post-approval code change that violates the design or these instructions must be reflected back in the relevant
  documentation to keep it up to date.
- All compiler warnings must be resolved before a change is considered complete. Never leave a warning unaddressed.

### Tooling

1. Use the `gh` CLI anytime you need to verify something on GitHub — workflow runs, releases, tags, PRs, issues, or
   repository state. Take advantage of its commands and capabilities instead of guessing or relying on assumptions.
2. `deepwiki` mcp gives access to context-gathering and question-answering tools for all GitHub repos indexed on
   DeepWiki.com. DeepWiki auto-generates wiki-style documentation for arbitrary public codebases, so this server is
   about understanding any GitHub project, including ones with no official docs of their own.
3. `microsoftdocs/mcp` mcp is scoped to Microsoft's own first-party material. It enables clients to bring trusted and
   up-to-date information directly from Microsoft's official documentation, and it
   allows agents to search through documentation, fetch a complete article, and search through code samples.

### Commit Messages

This project follows [Conventional Commits](https://www.conventionalcommits.org/).

## VERSION File

The single source of truth for the app version is the `VERSION` file at the repository root.
The `.csproj` reads from it at build time — there is no need to edit version numbers in XML.

```
VERSION          ← contains e.g. "1.2.0" (no "v" prefix, no trailing newline beyond one LF)
```

### How to Bump the Version

1. You'll always be told about the next version number. If not, ask the user or maintainer what the new version should
   be.
2. Commit existing changes first independently of the version bump. The version bump must be a separate commit on top of
   a clean working tree.
3. Edit the `VERSION` file with the new version number.
4. Commit: `git commit -am "chore: bump version to X.Y.Z"`
5. Push: `git push origin main`

> **⚠️ CRITICAL:** The VERSION file change must be in its own **single, dedicated commit** with
> exactly the message `chore: bump version to X.Y.Z`. Never combine it with other changes.
> This is required for the CI tagging pipeline to work correctly.

The CI pipeline (`.github/workflows/tag.yml`) detects the VERSION change, creates and pushes
`vX.Y.Z` tag automatically, which then triggers the release workflow
(`.github/workflows/release.yml`) to build AOT binaries and publish a GitHub Release.