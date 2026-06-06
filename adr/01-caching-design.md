# Caching Layer — Design Discussion

> Status: **brainstorm / not yet implemented.** This captures the design conversation so we can
> build on it later. Nothing here is final.

## Goal

Speed up a run by avoiding redundant work — primarily redundant **network** work in the update-check
stage. The tool currently re-scans and re-checks everything from scratch on every run (all state is
in-memory). We want a persistent cache that lets a warm run skip HTTP calls it already made recently,
without sacrificing correctness or the user's trust in the "is my app outdated" verdict.

## Scope

- **In scope:** caching the *update-check* stage (Stage 2).
- **Out of scope (for now):** the security audit stage (Stage 3 / OSV + GHSA). That code is currently
  commented out, its functionality isn't stable, and it's extremely heavy to run. We'll revisit caching
  it later — when re-enabled it's actually the strongest cache candidate (key = `(package, version)` →
  CVE list, very stable per-version), but it's parked for now.
- **Discovery (Stage 1) is NOT cached** — see below.

---

## Reframe 1 — Discovery vs. Check are different problems

Two things could be cached, and they must be treated independently:

| Concern | Can we cache it? | Decision |
|---|---|---|
| **Existence** ("what apps are installed") | No — install/uninstall is invisible without a scan | **Always full-scan.** Don't cache. |
| **Update verdict** ("is app X outdated") | Yes, partially | **Cache the remote fact, TTL-gated.** See Reframe 3. |

The scan (filesystem + subprocess, local) is the *cheap* part. The slow part is the HTTP calls to
registries (GitHub, Homebrew, npm, …). So the cache's job is **not** "avoid scanning" — it's
**"given a fresh scan, skip redundant network checks."**

## Reframe 2 — Uninstall detection is a non-problem

The cache is only ever *consulted* for apps the current scan actually discovered. An orphaned entry for
a deleted app just sits there, harmless. Prune it lazily via a `last_seen` timestamp (evict anything not
seen in N days). **Correctness never depends on knowing about uninstalls.**

---

## Reframe 3 — Cache the remote fact, never the verdict

This is the core of the safe design. Decompose what a "check" produces into three things that change at
different rates and come from different sources:

| Value | Source each run | Cached? |
|---|---|---|
| **Installed version** | always fresh from the scan (local, cheap) | **No** |
| **Latest available version** | cache if entry is fresh; else HTTP | **Yes — with TTL** (+ timestamp, + ETag) |
| **Verdict** (outdated?) | always recomputed = `compare(installed, latest)` | **No — derived live** |

### Why not cache the verdict directly

Caching the verdict ("outdated → 1.1") breaks the moment the installed version changes between runs:

1. **Run 1:** App A installed = `1.0`. HTTP says latest = `1.1`. Cache verdict "outdated → 1.1".
2. User (or Homebrew) upgrades A to `1.1`.
3. **Run 2, within TTL:** scan reports installed = `1.1`. A blindly-read cached verdict still says
   **"outdated, 1.1 available"** — wrong; the app is now current.

Caching only `latest = 1.1` and recomputing `compare(1.1, 1.1)` → **"current"**, with zero HTTP. The
installed side is always fresh from the scan we already do, so this costs nothing. **The cache stores
remote facts only; the verdict is always derived live.** That is what makes TTL-gating safe.

---

## The TTL-gating mechanism

Per-entry rule:

- **Cache hit (fresh):** read `latest` from cache → compare against freshly-scanned `installed` →
  emit verdict. **No HTTP.**
- **Cache miss / expired:** HTTP → get `latest` → write `(latest, now, etag)` → emit verdict.
- **`--force`:** treat every entry as expired → recheck all over HTTP → overwrite all entries.

"Fresh" means `now - last_checked < TTL`.

### `--force` semantics

- Composes with the default run (outdated only) and with `--all`.
- A forced run **writes** fresh entries (resets every TTL clock) — it's the most authoritative data
  we'll ever have, so it should refresh the cache, not merely bypass it for one run.

### The one accepted tradeoff: bounded staleness

The only thing that can go stale is the `latest` side, bounded by the TTL. If A.2 ships while a fresh
cache still says A.1, we report A.1 until the entry expires. **This is acceptable for this domain:** the
tool is run casually and interactively, not as a daemon. Missing a release by a few hours until the next
refresh is low-stakes and self-correcting.

---

## Correctness & resilience concerns

### Auto-invalidate on tool version change
Store the app/schema version in the cache. If `apps` itself was upgraded since the cache was written,
parsing / version-comparison logic may have changed — **drop the cache** rather than trust it.

### Serve-stale-on-error
If an HTTP check *fails* (network down, 429, timeout), fall back to the **stale cached `latest`** instead
of showing nothing — flagged as stale in output. Makes the cache a resilience layer, not just a speed
layer. (Implies we keep expired entries around until eviction, rather than deleting on expiry.)
Mirrors `stale-if-error` semantics (RFC 5861).

### Transparency
Never *silently* skip a check. Surface freshness in output, e.g. `up to date (checked 3h ago)`. The
tool's whole job is trust; cache hits must be visible. `--force` / `--no-cache` available to override.

---

## Cache key & entry shape

- **Key** = stable per-app identity, independent of version:
  `(component/scanner, name, install-source[, install-path])`. **Version is a value, never part of the
  key** — otherwise every upgrade orphans the entry.
- **Entry (value):**
  - `latest_version`
  - `last_checked` (timestamp)
  - `etag` / `last_modified` (optional, for conditional requests)
  - `last_seen` (for lazy eviction)
  - app/schema version (for invalidation — could be global instead of per-entry)

---

## Storage: SQLite vs. flat JSON file

The dataset is tiny (hundreds of rows). Two options:

- **Flat JSON file** (source-generated `JsonSerializerContext`, the project's existing pattern):
  zero native dependency, trivially AOT-safe, atomic write via temp-file + rename. Load once into a
  `Dictionary` at startup, flush once at the end.
- **SQLite** (`Microsoft.Data.Sqlite` / SQLitePCLRaw): buys queryability, partial writes, concurrency,
  and scale we don't have; adds a native dependency and AOT/bundling considerations.

**Leaning flat-file.** It fits the "all state in-memory" philosophy: read once into memory at start,
every lookup is in-memory, write deltas back in one shot at the end. This also sidesteps any
write-contention against the concurrent streaming pipeline — we never touch disk mid-pipeline.

> Open: confirmed direction once we look at AOT/atomic-write details. (This was one of the deep-research
> angles we still want data on.)

Likely location alongside existing app data: `~/.local/share/apps/` (logs already live under
`~/.local/share/apps/log/`).

---

## HTTP freshness (optimization, secondary)

When a TTL expires, conditional requests can soften the recheck:

- Store `ETag` / `Last-Modified` per entry; replay as `If-None-Match` / `If-Modified-Since`.
- A `304 Not Modified` is cheaper to parse and bandwidth-friendly.
- **Caveat to verify:** how 304s interact with rate limits (notably the GitHub REST API — does a 304
  still consume primary rate-limit budget?). This was a deep-research angle; not yet confirmed.

Doesn't save the round trip, but plays nicer with rate limits. Secondary to the TTL gate.

---

## Tiered TTL (possible later refinement)

Different sources move at different speeds (npm fast; a stable SDK or Homebrew formula slower). Could
support per-component TTL overrides. **Start with a single global TTL**; add tiers only if measurement
justifies it.

- Proposed default TTL: **~12–24h** (open question — see below).

---

## Integration point — where the TTL gate lives

Two options for the pipeline:

- **A) Decorator** wrapping each `CheckAsync` — caching logic written once, cross-cutting. Needs a
  uniform contract: each checker exposes a stable cache key + how to read `latest` from its result.
- **B) In-checker** — each component's `CheckAsync` consults the cache itself. Fits the vertical-slice
  philosophy, but duplicates the TTL/read/write dance across every component.

**Leaning A** (decorator + thin contract) for DRY — pending a look at how uniform the checkers' "latest
version" outputs actually are.

---

## Open questions

1. **Default TTL** — 12h? 24h? What staleness window are we comfortable with as authors?
2. **Gate placement** — decorator (A) vs in-checker (B)? Depends on `CheckAsync` signature/result
   uniformity across components.
3. **Storage** — confirm flat-JSON over SQLite once AOT/atomic-write details are checked.
4. **Conditional requests** — worth the complexity in v1, or defer? Hinges on the GitHub 304/rate-limit
   behavior.
5. **Measurement first** — confirm the bottleneck is network (Stage 2), not the scan, before building.

## Suggested first step

Prototype on a single component: the installed/latest/verdict decomposition + TTL gate + flat-file
store. Measure the warm-run speedup, then generalize the mechanism across all checkers.
