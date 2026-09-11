# Latest-Version Oracle for macOS Apps

> Status: **accepted / implemented** (PR #63).

## Context

The tool only *checks* for updates — it never installs them. So for each app it needs one thing: the
**latest available version**, to compare against the installed one. How the app updates itself (its
channel) does not matter.

Many apps update through their own channel and carry no standard on-disk update signal:

- VS Code, Cursor: their own update service (`product.json` -> `update.code.visualstudio.com`).
- Claude, Slack, Discord: raw Squirrel.Mac (feed URL set at runtime, nothing on disk).
- Chrome, Google Gemini: Google Keystone / private endpoints.

None expose `SUFeedURL` (Sparkle) or `app-update.yml` (electron-updater), so the existing native
readers miss them. It is tempting to add a detector per channel.

## Decision

**The Homebrew cask API is the app-agnostic latest-version oracle.** A cask's `version` is the upstream
latest regardless of how the app updates itself, and it needs no local Homebrew install (the per-cask
JSON at `formulae.brew.sh/api/cask/{token}.json` is reachable directly).

**Do not build per-channel detectors** (VS Code `product.json`, Google Keystone `KSUpdateURL`, raw
Squirrel.Mac feed scraping). They are per-app or per-vendor, do not scale, and are redundant with the
cask oracle for a check-only tool.

Resolution order per app (first hit wins):

1. **Native on-disk source** — Sparkle `SUFeedURL`, electron-updater `app-update.yml`, Mac App Store
   receipt -> iTunes lookup. Authoritative and instant when present.
2. **Homebrew cask by token** — try a token from the bundle folder name first (e.g.
   `Visual Studio Code.app` -> `visual-studio-code`), then the display name.
3. **Homebrew cask by fuzzy token** — when derived tokens miss, match them against the local Homebrew
   cask-name list (segment-aware, ranked, capped) to recover vendor-prefixed / suffixed tokens
   (`Gemini` -> `google-gemini`, `GitHub Copilot` -> `github-copilot-app`).

Every cask candidate is **verified against the app's path / bundle id** before its version is trusted,
so a same-name but unrelated cask is rejected (`Gemini` does not resolve MacPaw's `Gemini 2`).
Pre-release channel variants (`*-beta`, `*-insiders`, ...) are skipped in the fuzzy step, since they may
share the stable app's bundle id and would be misreported as its update.

## Consequences

- Resolves any app that has a cask, regardless of how its display name maps to the token.
- Cask versions lag the true upstream release by hours to days (maintainer PR + `livecheck`). For a
  check-only tool this is an occasional release-day false-negative, not a wrong answer; native sources
  (step 1) avoid it where they exist.
- The fuzzy step reads Homebrew's local cache, so it is a no-op without Homebrew; direct-token and
  native resolution still work.
- An app with no cask and no native signal stays unresolved and is shown dimmed — correct, not a bug.
  Apple system apps are covered separately by `softwareupdate`.
