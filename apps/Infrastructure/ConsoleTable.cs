using System.Runtime.CompilerServices;

namespace apps.Infrastructure;

/// <summary>
/// Defines a single column in a <see cref="ConsoleTable{TRow}"/>.
/// <para>
/// Set <paramref name="fixedWidth"/> &gt; 0 for a fixed-width column.
/// Leave it at zero to make the column flexible — space is allocated proportionally by
/// <paramref name="weight"/> and clamped to [<paramref name="minWidth"/>, <paramref name="maxWidth"/>].
/// </para>
/// <para>
/// To hide a column, simply remove or comment out its entry from the column list passed to
/// <see cref="ConsoleTable{TRow}"/>. The row values for that column disappear automatically.
/// </para>
/// </summary>
public sealed class TableColumn<TRow>(
    string header,
    Func<TRow, int, string> render,
    int fixedWidth = 0,
    int minWidth = 10,
    int maxWidth = 60,
    float weight = 1f)
{
    /// <summary>The column header text.</summary>
    public string Header { get; } = header;

    /// <summary>Fixed display width in visible characters. Zero means the column is flexible.</summary>
    public int FixedWidth { get; } = fixedWidth;

    /// <summary>Minimum visible width, used only for flexible columns.</summary>
    public int MinWidth { get; } = minWidth;

    /// <summary>Maximum visible width, used only for flexible columns.</summary>
    public int MaxWidth { get; } = maxWidth;

    /// <summary>Relative weight for proportional space allocation among flexible columns.</summary>
    public float Weight { get; } = weight;

    /// <summary>
    /// Cell render delegate. Receives the row and the column's allocated visible width.
    /// Must return a string padded to exactly that width (ANSI codes carry zero display width).
    /// </summary>
    public Func<TRow, int, string> Render { get; } = render;

    /// <summary>True when this column uses a fixed width.</summary>
    public bool IsFixed => FixedWidth > 0;
}

/// <summary>
/// An ANSI-aware terminal table with typed, reorderable, hideable columns.
/// <para>
/// Column order is determined by the order they appear in <paramref name="columns"/>.
/// Reorder by moving entries; hide by removing or commenting out an entry —
/// the corresponding row values disappear automatically.
/// </para>
/// </summary>
public sealed class ConsoleTable<TRow>(
    IReadOnlyList<TableColumn<TRow>> columns,
    string separator = "  ",
    string emptyMessage = "No items found.")
{
    /// <summary>
    /// Renders the table to <see cref="Console.Out"/>.
    /// </summary>
    /// <param name="rows">All rows to display.</param>
    /// <param name="subtitleSelector">
    /// Optional. Returns a pre-styled subtitle line for a row (or <see langword="null"/> for single-line rows).
    /// Receives the row and the array of allocated column widths indexed by column position.
    /// </param>
    /// <param name="groupSelector">
    /// Optional. Returns the group label string for a row.
    /// When the label changes between consecutive rows a dim group separator is printed.
    /// </param>
    public void Render(
        IReadOnlyList<TRow> rows,
        Func<TRow, int[], string?>? subtitleSelector = null,
        Func<TRow, string?>? groupSelector = null)
    {
        if (rows.Count == 0)
        {
            Console.WriteLine(emptyMessage);
            return;
        }

        var termW = ProbeTerminalWidth();
        var widths = AllocateWidths(termW);
        var totalW = ComputeTotalWidth(widths);

        PrintHeader(widths);
        Console.WriteLine(AnsiStyle.Dim(new string('─', totalW)));

        string? lastGroup = null;
        var firstGroup = true;

        foreach (var row in rows)
        {
            var group = groupSelector?.Invoke(row);

            if (group != lastGroup)
            {
                if (!firstGroup)
                {
                    Console.WriteLine();
                }

                if (group is not null)
                {
                    var fill = Math.Max(0, totalW - group.Length - 4);
                    Console.WriteLine(AnsiStyle.Dim($"── {group} {new string('─', fill)}"));
                }

                lastGroup = group;
                firstGroup = false;
            }

            PrintRow(row, widths);

            var subtitle = subtitleSelector?.Invoke(row, widths);
            if (subtitle is not null)
            {
                Console.WriteLine(subtitle);
            }
        }
    }

    private void PrintHeader(int[] widths)
    {
        var parts = new string[columns.Count];

        for (var i = 0; i < columns.Count; i++)
        {
            parts[i] = AnsiStyle.Bold(columns[i].Header.PadRight(widths[i]));
        }

        Console.WriteLine(string.Join(separator, parts));
    }

    private void PrintRow(TRow row, int[] widths)
    {
        var parts = new string[columns.Count];

        for (var i = 0; i < columns.Count; i++)
        {
            parts[i] = columns[i].Render(row, widths[i]);
        }

        Console.WriteLine(string.Join(separator, parts));
    }

    private int ComputeTotalWidth(int[] widths)
    {
        var sum = 0;
        foreach (var w in widths)
        {
            sum += w;
        }
        return sum + Math.Max(0, columns.Count - 1) * separator.Length;
    }

    /// <summary>
    /// Proportionally distributes available terminal width among flexible columns,
    /// clamping each to its [<see cref="TableColumn{TRow}.MinWidth"/>, <see cref="TableColumn{TRow}.MaxWidth"/>] range.
    /// Fixed columns take their declared width unconditionally.
    /// </summary>
    private int[] AllocateWidths(int termW)
    {
        var n = columns.Count;
        var widths = new int[n];
        var sepTotal = Math.Max(0, n - 1) * separator.Length;

        var fixedUsed = 0;
        for (var i = 0; i < n; i++)
        {
            if (columns[i].IsFixed)
            {
                widths[i] = columns[i].FixedWidth;
                fixedUsed += widths[i];
            }
        }

        var available = Math.Max(0, termW - sepTotal - fixedUsed);

        var pending = new List<int>(n);
        for (var i = 0; i < n; i++)
        {
            if (!columns[i].IsFixed)
            {
                pending.Add(i);
            }
        }

        if (pending.Count == 0)
        {
            return widths;
        }

        var rem = available;

        // Two-pass proportional allocation: columns that hit their min/max are frozen out
        // and their clamped width is subtracted from the remaining budget before the next pass.
        while (pending.Count > 0)
        {
            var tw = 0f;
            foreach (var i in pending)
            {
                tw += columns[i].Weight;
            }

            if (tw <= 0f)
            {
                break;
            }

            var capped = new List<int>();

            foreach (var i in pending)
            {
                var share = (int)(columns[i].Weight / tw * rem);
                var clamped = Math.Clamp(share, columns[i].MinWidth, columns[i].MaxWidth);

                if (clamped != share)
                {
                    widths[i] = clamped;
                    capped.Add(i);
                }
            }

            if (capped.Count == 0)
            {
                // No more clamping — assign remaining share to every pending column.
                foreach (var i in pending)
                {
                    widths[i] = Math.Clamp(
                        (int)(columns[i].Weight / tw * rem),
                        columns[i].MinWidth,
                        columns[i].MaxWidth);
                }
                break;
            }

            foreach (var i in capped)
            {
                rem -= widths[i];
                pending.Remove(i);
            }
        }

        return widths;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ProbeTerminalWidth()
    {
        int termW;
        try
        {
            termW = Console.IsOutputRedirected ? 120 : Console.WindowWidth;
            if (termW <= 0)
            {
                termW = 120;
            }
        }
        catch
        {
            termW = 120;
        }
        return Math.Max(80, termW);
    }
}

