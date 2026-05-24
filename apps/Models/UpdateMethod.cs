namespace apps.Models;

/// <summary>
/// Update channel priority chain (highest = 1, lowest = 12).
/// Once recorded in the DB it is not re-evaluated unless --force is passed.
/// Numeric values are stored as enum name strings in the DB, so adding new values never
/// breaks existing rows.
/// </summary>
public enum UpdateMethod
{
    /// <summary>Priority 1: App Store via mas CLI</summary>
    AppStore = 1,

    /// <summary>Priority 2: Homebrew Cask</summary>
    HomebrewCask = 2,

    /// <summary>Priority 3: Homebrew Formula</summary>
    HomebrewFormula = 3,

    /// <summary>Priority 4: Sparkle appcast (SUFeedURL in Info.plist)</summary>
    Sparkle = 4,

    /// <summary>Priority 5: Electron auto-updater (app-update.yml — GitHub or generic feed)</summary>
    Electron = 5,

    /// <summary>Priority 6: GitHub Releases API</summary>
    GitHub = 6,

    /// <summary>Priority 7: MacPorts</summary>
    MacPorts = 7,

    /// <summary>Priority 8: Chocolatey package manager</summary>
    Chocolatey = 8,

    /// <summary>Priority 9: Package registries (NuGet, npm, Go proxy)</summary>
    PackageRegistry = 9,

    /// <summary>Priority 10: Specialised checkers (Docker Hub, VS Code, JetBrains, macOS SW Update)</summary>
    Specialised = 10,

    /// <summary>Priority 11: SDK-specific tools (dotnet sdk check, rustup check)</summary>
    Sdk = 11,

    /// <summary>Priority 12: No mechanism found — flagged for manual review</summary>
    None = 12,

    /// <summary>
    /// The app manages its own update lifecycle (e.g. a PWA / browser-hosted web app).
    /// No external update check is performed; the app self-updates through its host browser.
    /// </summary>
    SelfUpdate = 13
}