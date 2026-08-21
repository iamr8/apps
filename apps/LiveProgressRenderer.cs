using System.Diagnostics;
using System.Globalization;

using apps.Components.Audit;

namespace apps;

/// <summary>
/// Renders live scan/check progress and the results table to the terminal.
/// Uses ANSI sequences when connected to a real TTY; falls back to plain text.
/// Thread-safe: all writes use a <see cref="Lock"/>.
/// </summary>
public sealed class LiveProgressRenderer(IEnumerable<IScanner> scanners)
{
    private readonly Lock _lock = new();

    private readonly IReadOnlyDictionary<string, IScanner> _scanners =
        scanners
            .DistinctBy(s => s.Name, StringComparer.Ordinal)
            .ToDictionary(s => s.Name, StringComparer.Ordinal);

    private readonly Dictionary<string, ChecklistRow> _checklistRows = new(StringComparer.Ordinal);
    private readonly List<string> _checklistOrder = [];

    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private const string Dash = "—";

    // Text of the current single-line status (scan or check progress).
    // Empty means no active line is displayed.
    private string _currentStatusLine = "";

    private readonly Stopwatch _phaseStopwatch = new();
    private readonly Stopwatch _checklistStopwatch = new();
    private string _checklistHeading = "";
    private int _renderedChecklistLines;
    private int _spinnerFrame;
    private bool _checklistVisible;
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

    /// <summary>Starts a checklist for the active scanners.</summary>
    public void StartScan(IReadOnlyList<IScanner> activeScanners)
    {
        lock (_lock)
        {
            _checklistRows.Clear();
            _checklistOrder.Clear();

            foreach (var scanner in activeScanners.DistinctBy(s => s.Name, StringComparer.Ordinal))
            {
                _checklistRows.Add(scanner.Name, new ChecklistRow(scanner));
                _checklistOrder.Add(scanner.Name);
            }

            _checklistHeading = "Scanning";
            _spinnerFrame = 0;
            _checklistVisible = true;
            _checklistStopwatch.Restart();
            _phaseStopwatch.Restart();
            RenderChecklist();
        }
    }

    /// <summary>Starts the check stage for each scanner with discovered items.</summary>
    public void StartCheck(IReadOnlyList<(IScanner Scanner, int Total)> groups)
    {
        lock (_lock)
        {
            var totals = groups.ToDictionary(g => g.Scanner.Name, g => g.Total, StringComparer.Ordinal);
            foreach (var row in _checklistRows.Values)
            {
                row.CheckStarted = true;
                row.CheckTotal = totals.GetValueOrDefault(row.Scanner.Name);
                row.State = row.ScanFailed
                    ? ChecklistProgressState.Failed
                    : row.CheckTotal == 0
                        ? ChecklistProgressState.Completed
                        : ChecklistProgressState.Waiting;
            }

            _checklistHeading = "Checking";
            _phaseStopwatch.Restart();
            RenderChecklist();
        }
    }

    /// <summary>Marks a scanner as active.</summary>
    public void RenderScannerActive(string scannerName)
    {
        lock (_lock)
        {
            if (_checklistRows.TryGetValue(scannerName, out var row))
            {
                row.State = ChecklistProgressState.Scanning;
                RenderChecklist();
            }
        }
    }

    /// <summary>Updates the number of items found by a scanner.</summary>
    public void RenderScannerProgress(string scannerName, int discovered)
    {
        lock (_lock)
        {
            if (_checklistRows.TryGetValue(scannerName, out var row))
            {
                row.Discovered = discovered;
                RenderChecklist();
            }
        }
    }

    /// <summary>Marks a scanner as waiting for the check stage.</summary>
    public void RenderScannerDone(string scannerName)
    {
        lock (_lock)
        {
            if (_checklistRows.TryGetValue(scannerName, out var row))
            {
                row.State = ChecklistProgressState.Waiting;
                RenderChecklist();
            }
        }
    }

    /// <summary>Marks a scanner as failed.</summary>
    public void RenderScannerFailed(string scannerName)
    {
        lock (_lock)
        {
            if (_checklistRows.TryGetValue(scannerName, out var row))
            {
                row.ScanFailed = true;
                row.State = ChecklistProgressState.Failed;
                RenderChecklist();
            }
        }
    }

    /// <summary>Marks the scan stage as complete while the checklist waits for checks.</summary>
    public void RenderScanComplete()
    {
        lock (_lock)
        {
            _checklistHeading = "Scan complete";
            RenderChecklist();
        }
    }

    /// <summary>Completes the checklist after a scan-only run.</summary>
    public void RenderDryRunComplete()
    {
        lock (_lock)
        {
            foreach (var row in _checklistRows.Values.Where(r => !r.ScanFailed))
            {
                row.State = ChecklistProgressState.Completed;
            }

            _checklistHeading = "Scan complete";
            FinalizeChecklist();
        }
    }

    /// <summary>Marks a scanner as actively checking its discovered items.</summary>
    public void RenderCheckActive(string scannerName)
    {
        lock (_lock)
        {
            if (_checklistRows.TryGetValue(scannerName, out var row))
            {
                row.State = ChecklistProgressState.Checking;
                RenderChecklist();
            }
        }
    }

    /// <summary>Updates one scanner's completed check, update, and failure counts.</summary>
    public void RenderCheckProgress(string scannerName, bool updateAvailable, bool failed)
    {
        lock (_lock)
        {
            if (!_checklistRows.TryGetValue(scannerName, out var row))
            {
                return;
            }

            row.Checked++;
            row.Updates += updateAvailable ? 1 : 0;
            row.Failures += failed ? 1 : 0;
            if (row.Checked >= row.CheckTotal)
            {
                row.State = row.ScanFailed || row.Failures > 0
                    ? ChecklistProgressState.Failed
                    : ChecklistProgressState.Completed;
            }

            RenderChecklist();
        }
    }

    /// <summary>Completes the checklist after all update checks finish.</summary>
    public void RenderCheckComplete()
    {
        lock (_lock)
        {
            foreach (var row in _checklistRows.Values)
            {
                if (row.CheckTotal > row.Checked)
                {
                    row.Failures += row.CheckTotal - row.Checked;
                }

                row.State = row.ScanFailed || row.Failures > 0
                    ? ChecklistProgressState.Failed
                    : ChecklistProgressState.Completed;
            }

            _checklistHeading = "Completed";
            FinalizeChecklist();
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
            if (_checklistVisible)
            {
                ClearChecklist();
                Console.Error.WriteLine(AnsiStyle.Red($"✗ {message}"));
                _renderedChecklistLines = 0;
                RenderChecklist();
                return;
            }

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
        => AnsiStyle.DarkGray($"[{seconds.ToString("F1", CultureInfo.InvariantCulture)}s]");

    /// <summary>Refreshes the checklist spinner and elapsed time during scanning.</summary>
    public Task RunScanTimerAsync(CancellationToken cancellationToken) => RunChecklistTimerAsync(cancellationToken);

    /// <summary>Refreshes the checklist spinner and elapsed time during update checks.</summary>
    public Task RunCheckTimerAsync(CancellationToken cancellationToken) => RunChecklistTimerAsync(cancellationToken);

    internal ChecklistProgressSnapshot GetChecklistSnapshot(string scannerName)
    {
        lock (_lock)
        {
            var row = _checklistRows[scannerName];
            return new ChecklistProgressSnapshot(
                row.State,
                row.Discovered,
                row.CheckTotal,
                row.Checked,
                row.Updates,
                row.Failures);
        }
    }

    private async Task RunChecklistTimerAsync(CancellationToken cancellationToken)
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
                if (!AnsiStyle.IsAnsi || !_checklistVisible)
                {
                    continue;
                }

                _spinnerFrame = (_spinnerFrame + 1) % SpinnerFrames.Length;
                RenderChecklist();
            }
        }
    }

    private void FinalizeChecklist()
    {
        _checklistStopwatch.Stop();
        if (AnsiStyle.IsAnsi)
        {
            RenderChecklist();
        }
        else
        {
            foreach (var line in BuildChecklistLines(int.MaxValue))
            {
                Console.Error.WriteLine(line);
            }
        }

        Console.Error.WriteLine();
        _checklistVisible = false;
        _renderedChecklistLines = 0;
    }

    private void RenderChecklist()
    {
        if (!AnsiStyle.IsAnsi || !_checklistVisible)
        {
            return;
        }

        var lines = BuildChecklistLines(GetConsoleWidth());
        ClearChecklist();
        foreach (var line in lines)
        {
            Console.Error.Write("\e[2K");
            Console.Error.WriteLine(line);
        }

        _renderedChecklistLines = lines.Length;
    }

    private void ClearChecklist()
    {
        if (!AnsiStyle.IsAnsi || _renderedChecklistLines == 0)
        {
            return;
        }

        Console.Error.Write($"\e[{_renderedChecklistLines}F");
        for (var i = 0; i < _renderedChecklistLines; i++)
        {
            Console.Error.Write("\e[2K");
            if (i < _renderedChecklistLines - 1)
            {
                Console.Error.Write("\e[1E");
            }
        }

        if (_renderedChecklistLines > 1)
        {
            Console.Error.Write($"\e[{_renderedChecklistLines - 1}F");
        }
    }

    private string[] BuildChecklistLines(int width)
    {
        var elapsed = _checklistStopwatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
        var heading = $"{_checklistHeading} [{elapsed}s]";
        var lines = new string[_checklistOrder.Count + 2];
        lines[0] = TruncateChecklistText(heading, width);
        lines[1] = "";

        for (var i = 0; i < _checklistOrder.Count; i++)
        {
            lines[i + 2] = FormatChecklistRow(_checklistRows[_checklistOrder[i]], width);
        }

        return lines;
    }

    private string FormatChecklistRow(ChecklistRow row, int width)
    {
        var indicator = row.State switch
        {
            ChecklistProgressState.Waiting => AnsiStyle.DarkGray("[ ]"),
            ChecklistProgressState.Scanning => AnsiStyle.White($"[{SpinnerFrames[_spinnerFrame]}]"),
            ChecklistProgressState.Checking => AnsiStyle.Yellow($"[{SpinnerFrames[_spinnerFrame]}]"),
            ChecklistProgressState.Completed => AnsiStyle.Green("[✓]"),
            ChecklistProgressState.Failed => AnsiStyle.Red("[!]"),
            _ => throw new InvalidOperationException($"Unknown checklist state: {row.State}")
        };
        var text = row.Scanner.ProgressLabel + FormatChecklistDetails(row);
        return indicator + " " + TruncateChecklistText(text, Math.Max(1, width - 4));
    }

    private static string FormatChecklistDetails(ChecklistRow row)
    {
        if (row.Discovered == 0 && row.State is ChecklistProgressState.Waiting or ChecklistProgressState.Scanning)
        {
            return "";
        }

        var count = FormatCount(row.Discovered, row.Scanner.ProgressItemNoun);
        if (!row.CheckStarted)
        {
            return $": {count}";
        }

        if (row.State == ChecklistProgressState.Checking)
        {
            return $": {count} · checking {row.Checked}/{row.CheckTotal}";
        }

        if (row.ScanFailed)
        {
            return $": {count} · scan failed";
        }

        if (row.Failures > 0)
        {
            var updates = row.Updates > 0 ? $" · {FormatCount(row.Updates, "update")}" : "";
            return $": {count}{updates} · {FormatCount(row.Failures, "failure")}";
        }

        if (row.CheckTotal == 0)
        {
            return $": {count} · nothing to check";
        }

        return row.Updates > 0
            ? $": {count} · {FormatCount(row.Updates, "update")}"
            : $": {count} · up to date";
    }

    private static string FormatCount(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static string TruncateChecklistText(string text, int width)
    {
        if (width <= 1)
        {
            return text[..Math.Min(text.Length, width)];
        }

        return text.Length <= width ? text : text[..(width - 1)] + "…";
    }

    private static int GetConsoleWidth()
    {
        try
        {
            return Math.Max(20, Console.WindowWidth - 1);
        }
        catch (IOException)
        {
            return 120;
        }
    }

    /// <summary>
    /// Renders a colorized summary report after the results table showing key pipeline statistics.
    /// </summary>
    public static void RenderSummary(
        int discovered,
        int @checked,
        int updatesAvailable,
        int pinned,
        int vulnerabilities,
        int errors,
        int @unchecked,
        TimeSpan elapsed)
    {
        Console.WriteLine();

        var parts = new List<string>
        {
            AnsiStyle.Bold(discovered.ToString()) + " discovered",
            AnsiStyle.Bold(@checked.ToString()) + " checked"
        };

        if (updatesAvailable > 0)
        {
            parts.Add(AnsiStyle.Yellow(AnsiStyle.Bold(updatesAvailable.ToString()) + " update" + (updatesAvailable == 1 ? "" : "s") + " available"));
        }
        else
        {
            parts.Add(AnsiStyle.Green("all up to date"));
        }

        if (pinned > 0)
        {
            parts.Add(AnsiStyle.Cyan(AnsiStyle.Bold(pinned.ToString()) + " pinned"));
        }

        if (vulnerabilities > 0)
        {
            parts.Add(AnsiStyle.Red(AnsiStyle.Bold(vulnerabilities.ToString()) + " vulnerable"));
        }
        else
        {
            parts.Add(AnsiStyle.Green("no vulnerabilities"));
        }

        if (errors > 0)
        {
            parts.Add(AnsiStyle.Red(AnsiStyle.Bold(errors.ToString()) + " error" + (errors == 1 ? "" : "s")));
        }

        if (@unchecked > 0)
        {
            parts.Add(AnsiStyle.DarkGray(AnsiStyle.Bold(@unchecked.ToString()) + " unchecked"));
        }

        var timeStr = elapsed.TotalSeconds < 60
            ? $"{elapsed.TotalSeconds:F1}s"
            : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s";

        parts.Add(AnsiStyle.DarkGray(timeStr));

        Console.WriteLine(string.Join(AnsiStyle.DarkGray(" · "), parts));
        Console.WriteLine();
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
            groupSelector: app => KindGroupLabel(app.App.Kind),
            nestedRowsSelector: app => app.SubApps is { Count: > 0 } ? app.SubApps : null);
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
            static (app, w) => app.App.Kind.ToCliString().PadRight(w),
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

    private string RenderNameCell(AppRecord record, int w)
    {
        var displayName = GetDisplayName(record);
        var hasError = !string.IsNullOrWhiteSpace(record.LastCheckError);
        var noVersion = string.IsNullOrWhiteSpace(record.App.InstalledVersion);
        var outdated = IsEffectivelyOutdated(record);
        var hasVulns = record.Vulnerabilities is { Count: > 0 };

        if (record.IsPinned)
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

        if (record.CheckFailed)
        {
            // Update status could not be determined — flag the row red, mirroring how outdated
            // rows are flagged green, rather than prefixing the name with a "?".
            return AnsiStyle.Red(AnsiStyle.Truncate(displayName.Trim(), w).PadRight(w));
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

        return cellText;
    }

    /// <summary>
    /// Returns the pre-styled subtitle line for a row, or <see langword="null"/> for single-line rows.
    /// Errors are shown in red; description subtitles in dim gray; CVEs in red below description.
    /// Update commands are shown in cyan below the description for outdated apps.
    /// </summary>
    private static string? GetFormattedSubtitle(AppRecord record, int nameW)
    {
        if (!string.IsNullOrWhiteSpace(record.LastCheckError))
        {
            return AnsiStyle.Red("  " + AnsiStyle.Truncate(record.LastCheckError.Trim(), nameW - 2));
        }

        var lines = new List<string>();
        var subtitle = GetSubtitle(record);

        if (subtitle is not null)
        {
            lines.Add(AnsiStyle.Dim("  " + AnsiStyle.Truncate(subtitle.Trim(), nameW - 2)));
        }

        if (record.Vulnerabilities is { Count: > 0 })
        {
            foreach (var vuln in record.Vulnerabilities)
            {
                var severity = FormatSeverity(vuln.Severity);
                var patchHint = vuln.PatchedVersion is not null ? $" (fix: {vuln.PatchedVersion})" : "";
                var summary = vuln.Summary is not null
                    ? " — " + AnsiStyle.Truncate(vuln.Summary.Trim(), nameW - vuln.Id.Length - severity.Length - patchHint.Length - 8)
                    : "";
                lines.Add(AnsiStyle.Red($"  {severity} {vuln.Id}{patchHint}{summary}"));
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
    private static string? GetSubtitle(AppRecord record)
    {
        if (record.App.Description is not null)
        {
            return record.App.Description;
        }

        if (record.App.PackageId is not null && !record.App.Name.Equals(record.App.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            return record.App.PackageId;
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
    private (string label, string? qualifier) GetSourceParts(AppRecord record)
    {
        // 1. Scanner-owned qualifier always wins (handles Docker, NuGet, VS Code extensions,
        //    Safari/Chrome extensions, etc. — without needing to match on raw scanner name strings).
        return (record.App.Identifier.DisplayName, record.App.Identifier.Qualifier);
        //
        // // 2. Special method cases with no checker representation.
        // if (app.UpdateMethod == UpdateMethod.None) return (Dash, null);
        // if (app.UpdateMethod == UpdateMethod.SelfUpdate) return GetPwaLabel(app.BundleId);
        //
        // // 3. Checker-owned source override (App Store, Homebrew Cask/Formula, GitHub, MacPorts, Chocolatey).
        // if (app.UpdateMethod is not null)
        // {
        //     foreach (var checker in _checkers)
        //     {
        //         if (checker.CanCheck(app) && checker.SourceOverride is { } src)
        //         {
        //             return src;
        //         }
        //     }
        // }
        //
        // // 4. Fallback: scanner display name, no qualifier (PackageRegistry, Sdk, Specialised checkers).
        // return (app.Identifier.DisplayName, null);
    }

    /// <summary>
    /// Builds the <c>Source</c> cell value, merging scanner origin and update method
    /// into a single human-friendly label. Qualifiers such as <c>(global)</c> are
    /// rendered in dark gray on capable terminals.
    /// </summary>
    private string FormatSourceCell(AppRecord record, int sourceW)
    {
        var (label, qualifier) = GetSourceParts(record);
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
    /// Returns the string to show in the Name column.
    /// Scanners with <see cref="IScanner.StripTagFromDisplayName"/> strip the colon-delimited tag
    /// (e.g. Docker <c>repo:tag</c> → <c>repo</c>).
    /// VS Code extensions: shows the marketplace display name (Description) rather than the extension ID.
    /// Everything else: uses <see cref="AppRecord.Name"/> directly.
    /// </summary>
    private string GetDisplayName(AppRecord record)
    {
        if (_scanners.TryGetValue(record.App.Identifier.Name, out var scanner)
            && scanner.StripTagFromDisplayName
            && record.App.Name is { } rawName)
        {
            var colonIdx = rawName.IndexOf(':');
            if (colonIdx > 0)
            {
                return rawName[..colonIdx];
            }
        }

        return record.App.Name ?? "";
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
    private static bool IsEffectivelyOutdated(AppRecord record)
    {
        if (!record.UpdateAvailable)
        {
            return false;
        }

        var installed = record.App.InstalledVersion?.Trim();
        var latest = record.App.LatestVersion?.Trim();

        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest))
        {
            return record.UpdateAvailable;
        }

        // When the latest version is a content-addressed hash (e.g. Docker sha256), ordering
        // is meaningless — trust UpdateAvailable directly regardless of the installed string.
        if (latest.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return record.UpdateAvailable;
        }

        if (string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase))
        {
            return record.UpdateAvailable;
        }

        return VersionComparer.Compare(installed, latest) < 0;
    }

    /// <summary>
    /// Builds the single <c>Version</c> cell value.
    /// Up-to-date rows show just the installed version.
    /// Outdated rows show <c><yellow>current</yellow> → <green>latest</green></c>
    /// padded to <paramref name="versionW"/> display columns.
    /// </summary>
    private static string BuildVersionCell(AppRecord record, int versionW, bool outdated)
    {
        var installed = (record.App.InstalledVersion ?? Dash).Trim().Split(',')[0];

        if (record.CheckFailed)
        {
            return AnsiStyle.Red(AnsiStyle.Truncate(installed, versionW).PadRight(versionW));
        }

        if (!outdated)
        {
            return AnsiStyle.Truncate(installed, versionW).PadRight(versionW);
        }

        var latest = (record.App.LatestVersion ?? Dash).Trim().Split(',')[0];
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
    /// Returns the update command for package-registry-based apps, dispatched by scanner type.
    /// </summary>
    private static string? GetRegistryUpdateCommand(AppRecord record)
    {
        var detail = record.App.UpdateInfo;
        if (detail is null)
        {
            return null;
        }

        return record.App.Identifier.Name switch
        {
            "NuGet" => $"dotnet tool update -g {detail}",
            "NugetLocalTools" => $"dotnet tool update {detail}",
            "npm" => $"npm update -g {detail}",
            "GoTools" => $"go install {detail}@latest",
            _ => null
        };
    }

    /// <summary>
    /// Extracts the cask token from the detail string.
    /// Catalog-matched entries use format <c>"catalog:{token}:{version}"</c>; returns just the token.
    /// </summary>
    private static string ExtractCaskToken(string detail)
    {
        if (!detail.StartsWith("catalog:", StringComparison.Ordinal))
        {
            return detail;
        }

        var afterPrefix = detail.AsSpan("catalog:".Length);
        var colonIdx = afterPrefix.IndexOf(':');
        return colonIdx > 0 ? afterPrefix[..colonIdx].ToString() : afterPrefix.ToString();
    }

    private sealed class ChecklistRow(IScanner scanner)
    {
        public IScanner Scanner { get; } = scanner;
        public ChecklistProgressState State { get; set; } = ChecklistProgressState.Waiting;
        public int Discovered { get; set; }
        public int CheckTotal { get; set; }
        public int Checked { get; set; }
        public int Updates { get; set; }
        public int Failures { get; set; }
        public bool CheckStarted { get; set; }
        public bool ScanFailed { get; set; }
    }
}

internal enum ChecklistProgressState
{
    Waiting,
    Scanning,
    Checking,
    Completed,
    Failed
}

internal readonly record struct ChecklistProgressSnapshot(
    ChecklistProgressState State,
    int Discovered,
    int CheckTotal,
    int Checked,
    int Updates,
    int Failures);
