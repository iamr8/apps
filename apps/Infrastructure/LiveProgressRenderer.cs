using System.Diagnostics;

using apps.Checkers;
using apps.Components.Audit;
using apps.Infrastructure.Logging;
using apps.Models;
using apps.Scanners;

namespace apps.Infrastructure;

/// <summary>
/// Renders live scan/check progress and the results table to the terminal.
/// Uses ANSI sequences when connected to a real TTY; falls back to plain text.
/// Thread-safe: all writes use a <see cref="Lock"/>.
/// </summary>
public sealed class LiveProgressRenderer(
    IEnumerable<IUpdateChecker> checkers,
    IEnumerable<IScanner> scanners,
    IEnumerable<IProjectLevelScanner> projectLevelScanners)
{
    private readonly Lock _lock = new();
    private readonly IReadOnlyList<IUpdateChecker> _checkers = checkers.ToArray();

    private readonly IReadOnlyDictionary<string, IScanner> _scanners =
        scanners
            .Concat(projectLevelScanners)
            .DistinctBy(s => s.Name, StringComparer.Ordinal)
            .ToDictionary(s => s.Name, StringComparer.Ordinal);

    private const string Dash = "—";
    private const string SelfUpdateLabel = "Self Update";

    // Text of the current single-line status (scan or check progress).
    // Empty means no active line is displayed.
    private string _currentStatusLine = "";

    private int _totalScanners;
    private int _completedScanners;
    private int _totalToCheck;

    private readonly Stopwatch _phaseStopwatch = new();
    private int _checkDone;
    private int _auditDone;
    private int _auditTotal;

    /// <summary>Clears the terminal screen before a fresh pipeline run.</summary>
    public static void RenderClear()
    {
        if (AnsiStyle.IsAnsi)
        {
            Console.Write("\e[2J\e[H");
        }
        else
        {
            Console.Clear();
        }
    }

    /// <summary>Sets the total number of active scanners so the progress bar knows its denominator.</summary>
    public void SetScannerCount(int total)
    {
        lock (_lock)
        {
            _totalScanners = total;
            _completedScanners = 0;
            _phaseStopwatch.Restart();
        }
    }

    /// <summary>Sets the total number of apps that will be checked so the check progress bar is accurate.</summary>
    public void SetCheckTotal(int total)
    {
        lock (_lock)
        {
            _totalToCheck = total;
            _phaseStopwatch.Restart();
        }
    }

    /// <summary>
    /// Updates the single scan-progress line in-place while scanners are active.
    /// Shows a progress bar indicating how many scanners have completed.
    /// On a real TTY the line is overwritten via ANSI; on redirected output a new line is
    /// printed only for the first scanner to avoid polluting piped output.
    /// </summary>
    public void RenderScannerActive(string scannerName)
    {
        lock (_lock)
        {
            var bar = AnsiStyle.ProgressBar(_completedScanners, _totalScanners);
            var label = AnsiStyle.Cyan("Scanning");
            var elapsed = FormatElapsed(_phaseStopwatch.Elapsed.TotalSeconds);
            var line = $"{bar}  {label} {AnsiStyle.Bold(scannerName)}… {elapsed}";
            var firstLine = _currentStatusLine.Length == 0;
            _currentStatusLine = line;

            if (AnsiStyle.IsAnsi)
            {
                Console.Error.Write($"\r\e[2K{line}");
            }
            else if (firstLine)
            {
                Console.Error.WriteLine($"● Scanning {scannerName}…");
            }
        }
    }

    /// <summary>Marks a scanner as completed and refreshes the progress bar.</summary>
    public void RenderScannerDone(string scannerName)
    {
        lock (_lock)
        {
            _completedScanners++;

            var bar = AnsiStyle.ProgressBar(_completedScanners, _totalScanners);
            var label = AnsiStyle.Cyan("Scanning");
            var elapsed = FormatElapsed(_phaseStopwatch.Elapsed.TotalSeconds);
            var line = $"{bar}  {label} {AnsiStyle.Dim(scannerName)} ✓ {elapsed}";
            _currentStatusLine = line;

            if (AnsiStyle.IsAnsi)
            {
                Console.Error.Write($"\r\e[2K{line}");
            }
        }
    }

    /// <summary>Clears the scan-progress line and prints the total discovered count.</summary>
    public void RenderScanComplete(int total)
    {
        lock (_lock)
        {
            ClearStatusLine();
            var bar = AnsiStyle.ProgressBar(_totalScanners, _totalScanners);
            var elapsed = FormatElapsed(_phaseStopwatch.Elapsed.TotalSeconds);
            Console.Error.WriteLine($"{bar}  {AnsiStyle.Green("✓")} Discovered {AnsiStyle.Bold(total.ToString())} apps {elapsed}");
            _currentStatusLine = "";
        }
    }

    /// <summary>
    /// Updates the single check-progress line in-place as each result arrives.
    /// Shows a progress bar indicating how many checks have completed out of the total.
    /// </summary>
    public void RenderCheckActive(int done)
    {
        lock (_lock)
        {
            _checkDone = done;
            var bar = AnsiStyle.ProgressBar(done, _totalToCheck);
            var label = AnsiStyle.Magenta("Checking");
            var elapsed = FormatElapsed(_phaseStopwatch.Elapsed.TotalSeconds);
            var line = $"{bar}  {label} {AnsiStyle.Bold(done.ToString())}/{_totalToCheck} apps… {elapsed}";
            _currentStatusLine = line;

            if (AnsiStyle.IsAnsi)
            {
                Console.Error.Write($"\r\e[2K{line}");
            }
        }
    }

    /// <summary>
    /// Clears the check-progress line and prints a summary of the check phase.
    /// </summary>
    public void RenderCheckComplete(int total, int updates, int errors)
    {
        lock (_lock)
        {
            ClearStatusLine();
            _currentStatusLine = "";

            var bar = AnsiStyle.ProgressBar(total, total);
            var elapsed = FormatElapsed(_phaseStopwatch.Elapsed.TotalSeconds);
            var updateStr = updates > 0
                ? AnsiStyle.Yellow($"{updates} update{(updates == 1 ? "" : "s")} available")
                : AnsiStyle.Green("up to date");
            var errorPart = errors > 0 ? "  " + AnsiStyle.Red($"{errors} error{(errors == 1 ? "" : "s")}") : "";
            Console.Error.WriteLine($"{bar}  {AnsiStyle.Green("✓")} Checked {AnsiStyle.Bold(total.ToString())} apps — {updateStr}{errorPart} {elapsed}");
        }
    }

    /// <summary>
    /// Writes a pre-formatted log line to stderr, properly interleaving with any active
    /// status line. Called exclusively by <see cref="RendererConsoleSink"/>.
    /// </summary>
    public void WriteLogLine(string text)
    {
        lock (_lock)
        {
            ClearStatusLine();
            Console.Error.WriteLine(text);
            RestoreStatusLine();
        }
    }

    /// <summary>
    /// Renders all tracked apps as a formatted table to stdout.
    /// Called after the scan + check pipeline completes.
    /// </summary>
    public void RenderTable(IReadOnlyList<AppRecord> apps)
    {
        PrintTableFmt(apps);
    }

    /// <summary>Prints an inline error message in red to stderr, preserving the active status line.</summary>
    public void RenderError(string message)
    {
        lock (_lock)
        {
            ClearStatusLine();
            Console.Error.WriteLine(AnsiStyle.Red($"✗ {message}"));
            RestoreStatusLine();
        }
    }

    /// <summary>Prints a styled phase-start indicator (e.g. "Resolving update methods…").</summary>
    public void RenderPhaseStart(string message)
    {
        lock (_lock)
        {
            ClearStatusLine();
            _phaseStopwatch.Restart();
            var styled = $"{AnsiStyle.Cyan("●")} {AnsiStyle.Dim(message)}";
            _currentStatusLine = styled;

            if (AnsiStyle.IsAnsi)
            {
                Console.Error.Write($"\r\e[2K{styled}");
            }
            else
            {
                Console.Error.WriteLine(message);
            }
        }
    }

    /// <summary>
    /// Updates the single resolver-progress line in-place, showing a progress bar with
    /// the current step label, completion count, and elapsed seconds.
    /// </summary>
    public void RenderResolverProgress(int done, int total, string stepLabel, double elapsedSeconds = 0)
    {
        lock (_lock)
        {
            var bar = AnsiStyle.ProgressBar(done, total);
            var label = AnsiStyle.Cyan("Resolving");
            var elapsed = elapsedSeconds > 0 ? $" {FormatElapsed(elapsedSeconds)}" : "";
            var line = $"{bar}  {label} {AnsiStyle.Dim(stepLabel)} ({done}/{total}){elapsed}";
            _currentStatusLine = line;

            if (AnsiStyle.IsAnsi)
            {
                Console.Error.Write($"\r\e[2K{line}");
            }
        }
    }

    /// <summary>Clears the resolver-progress line and prints a completed progress bar with summary.</summary>
    public void RenderResolverComplete(int totalSteps, int resolvedCount)
    {
        lock (_lock)
        {
            ClearStatusLine();
            var bar = AnsiStyle.ProgressBar(totalSteps, totalSteps);
            var elapsed = FormatElapsed(_phaseStopwatch.Elapsed.TotalSeconds);
            Console.Error.WriteLine($"{bar}  {AnsiStyle.Green("✓")} Resolved update methods for {AnsiStyle.Bold(resolvedCount.ToString())} apps {elapsed}");
            _currentStatusLine = "";
        }
    }

    /// <summary>Clears the phase indicator and prints a styled completion message with elapsed time.</summary>
    public void RenderPhaseEnd(string message)
    {
        lock (_lock)
        {
            ClearStatusLine();
            var elapsed = FormatElapsed(_phaseStopwatch.Elapsed.TotalSeconds);
            Console.Error.WriteLine($"{AnsiStyle.Green("✓")} {message} {elapsed}");
            _currentStatusLine = "";
        }
    }

    /// <summary>Updates the audit progress line in-place with batch count and elapsed time.</summary>
    public void RenderAuditProgress(int done, int total)
    {
        lock (_lock)
        {
            _auditDone = done;
            _auditTotal = total;
            var bar = AnsiStyle.ProgressBar(done, total);
            var label = AnsiStyle.Cyan("Auditing");
            var elapsed = FormatElapsed(_phaseStopwatch.Elapsed.TotalSeconds);
            var line = $"{bar}  {label} {AnsiStyle.Bold(done.ToString())}/{total} batches… {elapsed}";
            _currentStatusLine = line;

            if (AnsiStyle.IsAnsi)
            {
                Console.Error.Write($"\r\e[2K{line}");
            }
        }
    }

    /// <summary>Clears the audit progress line and prints a completed progress bar with summary.</summary>
    public void RenderAuditComplete(int total, int vulnerableCount)
    {
        lock (_lock)
        {
            ClearStatusLine();
            var bar = AnsiStyle.ProgressBar(total, total);
            var elapsed = FormatElapsed(_phaseStopwatch.Elapsed.TotalSeconds);
            var vulnStr = vulnerableCount > 0
                ? AnsiStyle.Red($"{vulnerableCount} vulnerable package{(vulnerableCount == 1 ? "" : "s")} found")
                : AnsiStyle.Green("no vulnerabilities found");
            Console.Error.WriteLine($"{bar}  {AnsiStyle.Green("✓")} Audit complete — {vulnStr} {elapsed}");
            _currentStatusLine = "";
        }
    }

    /// <summary>
    /// Background task that refreshes the audit progress line every 100ms with updated elapsed time.
    /// </summary>
    public async Task RunAuditTimerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            lock (_lock)
            {
                if (!AnsiStyle.IsAnsi || _currentStatusLine.Length == 0)
                {
                    continue;
                }

                var bar = AnsiStyle.ProgressBar(_auditDone, _auditTotal);
                var label = AnsiStyle.Cyan("Auditing");
                var elapsed = FormatElapsed(_phaseStopwatch.Elapsed.TotalSeconds);
                var line = $"{bar}  {label} {AnsiStyle.Bold(_auditDone.ToString())}/{_auditTotal} batches… {elapsed}";
                _currentStatusLine = line;
                Console.Error.Write($"\r\e[2K{line}");
            }
        }
    }

    /// <summary>Sets the audit totals for the timer background task.</summary>
    public void SetAuditTotal(int total)
    {
        lock (_lock)
        {
            _auditTotal = total;
            _auditDone = 0;
            _phaseStopwatch.Restart();
        }
    }

    private void ClearStatusLine()
    {
        if (AnsiStyle.IsAnsi && _currentStatusLine.Length > 0)
        {
            Console.Error.Write("\r\e[2K");
        }
    }

    private void RestoreStatusLine()
    {
        if (AnsiStyle.IsAnsi && _currentStatusLine.Length > 0)
        {
            Console.Error.Write(_currentStatusLine);
        }
    }

    private static string FormatElapsed(double seconds)
        => AnsiStyle.DarkGray($"[{seconds:F1}s]");

    /// <summary>
    /// Background task that refreshes the scan progress line every 100ms with updated elapsed time.
    /// </summary>
    public async Task RunScanTimerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            lock (_lock)
            {
                if (!AnsiStyle.IsAnsi || _currentStatusLine.Length == 0)
                {
                    continue;
                }

                var bar = AnsiStyle.ProgressBar(_completedScanners, _totalScanners);
                var label = AnsiStyle.Cyan("Scanning");
                var elapsed = FormatElapsed(_phaseStopwatch.Elapsed.TotalSeconds);
                var line = $"{bar}  {label} {elapsed}";
                _currentStatusLine = line;
                Console.Error.Write($"\r\e[2K{line}");
            }
        }
    }

    /// <summary>
    /// Background task that refreshes the check progress line every 100ms with updated elapsed time.
    /// </summary>
    public async Task RunCheckTimerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            lock (_lock)
            {
                if (!AnsiStyle.IsAnsi || _currentStatusLine.Length == 0)
                {
                    continue;
                }

                var bar = AnsiStyle.ProgressBar(_checkDone, _totalToCheck);
                var label = AnsiStyle.Magenta("Checking");
                var elapsed = FormatElapsed(_phaseStopwatch.Elapsed.TotalSeconds);
                var line = $"{bar}  {label} {AnsiStyle.Bold(_checkDone.ToString())}/{_totalToCheck} apps… {elapsed}";
                _currentStatusLine = line;
                Console.Error.Write($"\r\e[2K{line}");
            }
        }
    }

    private void PrintTableFmt(IReadOnlyList<AppRecord> apps)
    {
        if (apps.Count == 0)
        {
            Console.WriteLine("No apps found. Run `apps` to scan.");
            return;
        }

        BuildTable().Render(
            apps,
            subtitleSelector: (app, widths) => GetFormattedSubtitle(app, widths[0]),
            groupSelector: app => KindGroupLabel(app.Kind));
    }

    /// <summary>
    /// Builds the results table. Column order is the display order — reorder freely.
    /// To hide a column, comment out its entry; its row values disappear automatically.
    /// </summary>
    private ConsoleTable<AppRecord> BuildTable() => new(
    [
        new TableColumn<AppRecord>(
            "Name",
            RenderNameCell,
            minWidth: 10,
            maxWidth: 60,
            weight: 2f),
        new TableColumn<AppRecord>(
            "Kind",
            static (app, w) => app.Kind.ToCliString().PadRight(w),
            fixedWidth: 9),
        new TableColumn<AppRecord>(
            "Source",
            FormatSourceCell,
            minWidth: 6,
            maxWidth: 35,
            weight: 1f),
        // new TableColumn<AppRecord>(
        //     "Update Method",
        //     (app, w) => GetCheckerDisplayName(app).PadRight(w),
        //     fixedWidth: 18),
        new TableColumn<AppRecord>(
            "Version",
            (app, w) => BuildVersionCell(app, w, IsEffectivelyOutdated(app)),
            minWidth: 36,
            maxWidth: 50,
            weight: 0.75f),
    ]);

    private string RenderNameCell(AppRecord app, int w)
    {
        var displayName = GetDisplayName(app);
        var hasError = !string.IsNullOrWhiteSpace(app.LastCheckError);
        var noVersion = string.IsNullOrWhiteSpace(app.InstalledVersion);
        var noMethod = app.UpdateMethod is null or UpdateMethod.None;
        var outdated = IsEffectivelyOutdated(app);
        var hasVulns = app.Vulnerabilities is { Count: > 0 };

        if (app.IsPinned)
        {
            var nameRaw = displayName.Trim();
            var pinnedSuffix = " <pinned>";
            var combined = nameRaw + pinnedSuffix;
            var truncated = AnsiStyle.Truncate(combined, w);
            var namePartLen = truncated.Length - pinnedSuffix.Length;

            if (namePartLen < 0)
            {
                return AnsiStyle.Truncate(combined, w).PadRight(w);
            }

            var namePart = truncated[..namePartLen];
            var pad = new string(' ', Math.Max(0, w - combined.Length));
            return namePart + AnsiStyle.Cyan(pinnedSuffix) + pad;
        }

        var cellText = AnsiStyle.Truncate(displayName.Trim(), w).PadRight(w);

        if (hasVulns || hasError || noVersion)
        {
            return AnsiStyle.Red(cellText);
        }

        if (outdated)
        {
            return AnsiStyle.Green(cellText);
        }

        if (noMethod)
        {
            return AnsiStyle.Yellow(cellText);
        }

        return cellText;
    }

    /// <summary>
    /// Returns the pre-styled subtitle line for a row, or <see langword="null"/> for single-line rows.
    /// Errors are shown in red; description subtitles in dim gray; CVEs in red below description.
    /// Update commands are shown in cyan below the description for outdated apps.
    /// </summary>
    private string? GetFormattedSubtitle(AppRecord app, int nameW)
    {
        if (!string.IsNullOrWhiteSpace(app.LastCheckError))
        {
            return AnsiStyle.Red("  " + AnsiStyle.Truncate(app.LastCheckError.Trim(), nameW - 2));
        }

        var lines = new List<string>();
        var subtitle = GetSubtitle(app);

        if (subtitle is not null)
        {
            lines.Add(AnsiStyle.Dim("  " + AnsiStyle.Truncate(subtitle.Trim(), nameW - 2)));
        }

        if (IsEffectivelyOutdated(app))
        {
            var cmd = GetUpdateCommand(app);
            if (cmd is not null)
            {
                lines.Add(AnsiStyle.Cyan("  " + AnsiStyle.Truncate(cmd, nameW - 2)));
            }
        }

        if (app.Vulnerabilities is { Count: > 0 })
        {
            foreach (var vuln in app.Vulnerabilities)
            {
                var severity = FormatSeverity(vuln.Severity);
                var summary = vuln.Summary is not null
                    ? " — " + AnsiStyle.Truncate(vuln.Summary.Trim(), nameW - vuln.Id.Length - severity.Length - 8)
                    : "";
                lines.Add(AnsiStyle.Red($"  {severity} {vuln.Id}{summary}"));
            }
        }

        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }

    private static string FormatSeverity(VulnerabilitySeverity severity)
    {
        return severity switch
        {
            VulnerabilitySeverity.Critical => "[CRITICAL]",
            VulnerabilitySeverity.High => "[HIGH]",
            VulnerabilitySeverity.Medium => "[MEDIUM]",
            VulnerabilitySeverity.Low => "[LOW]",
            _ => "[CVE]"
        };
    }

    /// <summary>
    /// Returns the dim subtitle to show on the second line of a row, or null for single-line rows.
    /// Extensions: plugin/extension ID (shown under the display name).
    /// Other apps: description text (when present) under the name.
    /// </summary>
    private string? GetSubtitle(AppRecord app)
    {
        if (app.Kind == AppKind.Extension)
        {
            // VS Code: Description = display name, Name = extensionId → show Name as subtitle.
            if (!string.IsNullOrEmpty(app.Description))
            {
                return app.Name;
            }

            // JetBrains: Name = display name, UpdateMethodDetail = plugin ID.
            if (!string.IsNullOrEmpty(app.UpdateMethodDetail) && app.UpdateMethodDetail != app.Name)
            {
                return app.UpdateMethodDetail;
            }
        }
        else if (!string.IsNullOrEmpty(app.Description))
        {
            return app.Description;
        }

        return null;
    }

    /// <summary>
    /// Resolves a PWA label from the app's bundle ID prefix.
    /// Falls back to a generic <c>PWA</c> label when the host browser is unrecognised.
    /// </summary>
    private static (string label, string? qualifier) GetPwaLabel(string? bundleId)
    {
        if (bundleId is null)
        {
            return ("PWA", null);
        }

        if (bundleId.StartsWith("com.apple.Safari.WebApp.", StringComparison.OrdinalIgnoreCase))
        {
            return ("Safari", "PWA");
        }

        if (bundleId.StartsWith("com.google.Chrome.app.", StringComparison.OrdinalIgnoreCase))
        {
            return ("Chrome", "PWA");
        }

        if (bundleId.StartsWith("com.microsoft.edgeapp.", StringComparison.OrdinalIgnoreCase))
        {
            return ("Edge", "PWA");
        }

        return ("PWA", null);
    }

    /// <summary>
    /// Returns the plain label and optional qualifier for the <c>Source</c> column.
    /// Priority order:
    /// <list type="number">
    ///   <item>Scanner's <see cref="IScanner.GetSourceQualifier"/> — when non-null, uses the scanner's display name + qualifier.</item>
    ///   <item>Special method cases with no checker: <see cref="UpdateMethod.None"/> and <see cref="UpdateMethod.SelfUpdate"/>.</item>
    ///   <item>Checker's <see cref="IUpdateChecker.SourceOverride"/> — overrides both label and qualifier.</item>
    ///   <item>Scanner display name with no qualifier (fallback for Specialised/PackageRegistry/Sdk checkers).</item>
    /// </list>
    /// </summary>
    private (string label, string? qualifier) GetSourceParts(AppRecord app)
    {
        // 1. Scanner-owned qualifier always wins (handles Docker, NuGet, VS Code extensions,
        //    Safari/Chrome extensions, etc. — without needing to match on raw scanner name strings).
        if (_scanners.TryGetValue(app.Scanner, out var scanner))
        {
            var scannerQualifier = scanner.GetSourceQualifier(app.Kind);
            if (scannerQualifier is not null)
            {
                return (ScanLabel(app.Scanner), scannerQualifier);
            }
        }

        // 2. Special method cases with no checker representation.
        if (app.UpdateMethod == UpdateMethod.None) return (Dash, null);
        if (app.UpdateMethod == UpdateMethod.SelfUpdate) return GetPwaLabel(app.BundleId);

        // 3. Checker-owned source override (App Store, Homebrew Cask/Formula, GitHub, MacPorts, Chocolatey).
        if (app.UpdateMethod is not null)
        {
            foreach (var checker in _checkers)
            {
                if (checker.CanCheck(app) && checker.SourceOverride is { } src)
                {
                    return src;
                }
            }
        }

        // 4. Fallback: scanner display name, no qualifier (PackageRegistry, Sdk, Specialised checkers).
        return (ScanLabel(app.Scanner), null);
    }

    /// <summary>
    /// Builds the <c>Source</c> cell value, merging scanner origin and update method
    /// into a single human-friendly label. Qualifiers such as <c>(global)</c> are
    /// rendered in dark gray on capable terminals.
    /// </summary>
    private string FormatSourceCell(AppRecord app, int sourceW)
    {
        var (label, qualifier) = GetSourceParts(app);
        var full = qualifier is null ? label : $"{label} ({qualifier})";
        var truncated = AnsiStyle.Truncate(full.Trim(), sourceW).PadRight(sourceW);

        if (qualifier is null || !AnsiStyle.IsAnsi)
        {
            return truncated;
        }

        var suffix = $" ({qualifier})";
        var idx = truncated.IndexOf(suffix, StringComparison.Ordinal);
        if (idx < 0)
        {
            return truncated;
        }

        // Dark-gray only the qualifier; leave trailing pad spaces unstyled.
        var pad = truncated[(idx + suffix.Length)..];
        return truncated[..idx] + AnsiStyle.DarkGray(suffix) + pad;
    }

    /// <summary>
    /// Returns the display name for the <c>Update Method</c> column by finding the first
    /// registered checker whose <see cref="IUpdateChecker.CanCheck"/> returns true for the app.
    /// Falls back to a generic label when no checker matches (e.g. Self Update apps).
    /// </summary>
    private string GetCheckerDisplayName(AppRecord app)
    {
        if (app.UpdateMethod is null or UpdateMethod.None)
        {
            return Dash;
        }

        foreach (var checker in _checkers)
        {
            if (checker.CanCheck(app))
            {
                return checker.DisplayName;
            }
        }

        return app.UpdateMethod == UpdateMethod.SelfUpdate ? SelfUpdateLabel : (app.UpdateMethod.ToString() ?? Dash);
    }

    /// <summary>
    /// Returns the string to show in the Name column.
    /// Scanners with <see cref="IScanner.StripTagFromDisplayName"/> strip the colon-delimited tag
    /// (e.g. Docker <c>repo:tag</c> → <c>repo</c>).
    /// VS Code extensions: shows the marketplace display name (Description) rather than the extension ID.
    /// Everything else: uses <see cref="AppRecord.Name"/> directly.
    /// </summary>
    private string GetDisplayName(AppRecord app)
    {
        if (_scanners.TryGetValue(app.Scanner, out var scanner)
            && scanner.StripTagFromDisplayName
            && app.Name is { } rawName)
        {
            var colonIdx = rawName.IndexOf(':');
            if (colonIdx > 0)
            {
                return rawName[..colonIdx];
            }
        }

        if (app.Kind == AppKind.Extension && !string.IsNullOrEmpty(app.Description))
        {
            return app.Description;
        }

        return app.Name ?? "";
    }

    /// <summary>Resolves the scanner's display name from the registered scanner map, falling back to the raw scanner string.</summary>
    private string ScanLabel(string scannerName)
        => _scanners.TryGetValue(scannerName, out var s) ? s.DisplayName : scannerName;

    /// <summary>
    /// Returns <see langword="true"/> when the app should be displayed as having an update.
    /// Guards against checkers that record <c>UpdateAvailable = true</c> but set
    /// <c>LatestVersion</c> to the same value as <c>InstalledVersion</c>.
    /// For sha256 digest-versioned artifacts (e.g. Docker), version ordering is meaningless —
    /// equality is the only relevant comparison, so <c>UpdateAvailable</c> is trusted directly.
    /// </summary>
    private static bool IsEffectivelyOutdated(AppRecord app)
    {
        if (!app.UpdateAvailable)
        {
            return false;
        }

        var installed = app.InstalledVersion?.Trim();
        var latest = app.LatestVersion?.Trim();

        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest))
        {
            return app.UpdateAvailable;
        }

        // When the latest version is a content-addressed hash (e.g. Docker sha256), ordering
        // is meaningless — trust UpdateAvailable directly regardless of the installed string.
        if (latest.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return app.UpdateAvailable;
        }

        if (string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase))
        {
            return app.UpdateAvailable;
        }

        return VersionComparer.Compare(installed, latest) < 0;
    }

    /// <summary>
    /// Builds the single <c>Version</c> cell value.
    /// Up-to-date rows show just the installed version.
    /// Outdated rows show <c><yellow>current</yellow> → <green>latest</green></c>
    /// padded to <paramref name="versionW"/> display columns.
    /// </summary>
    private static string BuildVersionCell(AppRecord app, int versionW, bool outdated)
    {
        var installed = (app.InstalledVersion ?? Dash).Trim();

        if (!outdated)
        {
            return AnsiStyle.Truncate(installed, versionW).PadRight(versionW);
        }

        var latest = (app.LatestVersion ?? Dash).Trim();
        const string arrow = " → ";

        // Split the available width roughly in half so both sides get equal room.
        var halfW = (versionW - arrow.Length) / 2;
        var instPart = AnsiStyle.Truncate(installed, halfW);
        var latPart = AnsiStyle.Truncate(latest, versionW - arrow.Length - halfW);
        var rawLen = instPart.Length + arrow.Length + latPart.Length;
        var pad = new string(' ', Math.Max(0, versionW - rawLen));

        return AnsiStyle.Yellow(instPart) + AnsiStyle.Dim(arrow) + AnsiStyle.Green(latPart) + pad;
    }

    private static string KindGroupLabel(AppKind kind) => kind.ToGroupLabel();

    /// <summary>
    /// Returns the shell command to update an app, or <see langword="null"/> when no actionable
    /// command applies (extensions, self-update apps, unresolved methods).
    /// </summary>
    private static string? GetUpdateCommand(AppRecord app)
    {
        if (app.Kind == AppKind.Extension)
        {
            return null;
        }

        var detail = app.UpdateMethodDetail;

        return app.UpdateMethod switch
        {
            UpdateMethod.AppStore when detail is not null => $"mas upgrade {detail}",
            UpdateMethod.HomebrewCask when detail is not null => $"brew upgrade --cask {detail}",
            UpdateMethod.HomebrewFormula when detail is not null => $"brew upgrade {detail}",
            UpdateMethod.MacPorts when detail is not null => $"sudo port upgrade {detail}",
            UpdateMethod.Chocolatey when detail is not null => $"choco upgrade {detail}",
            UpdateMethod.PackageRegistry => GetRegistryUpdateCommand(app),
            UpdateMethod.Specialised when app.Scanner == "Docker" && detail is not null => $"docker pull {detail}",
            UpdateMethod.Sdk when app.Scanner is "Dotnet" or "DotnetRuntime" => "brew upgrade dotnet-sdk",
            _ => null
        };
    }

    /// <summary>
    /// Returns the update command for package-registry-based apps, dispatched by scanner type.
    /// </summary>
    private static string? GetRegistryUpdateCommand(AppRecord app)
    {
        var detail = app.UpdateMethodDetail;
        if (detail is null)
        {
            return null;
        }

        return app.Scanner switch
        {
            "NuGet" => $"dotnet tool update -g {detail}",
            "NugetLocalTools" => $"dotnet tool update {detail}",
            "npm" => $"npm update -g {detail}",
            "GoTools" => $"go install {detail}@latest",
            _ => null
        };
    }
}