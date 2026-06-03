namespace apps;

/// <summary>
/// Compares version strings using a best-effort strategy:
///   1. SemVer (x.y.z[-pre][+build]) — tried first to honour pre-release ordering
///   2. System.Version for 4-part numeric versions (x.y.z.w)
///   3. Date-based versions (YYYYMMDD or YYYY.MM.DD)
///   4. Lexicographic fallback
/// Returns a negative number if a &lt; b, 0 if equal, positive if a &gt; b.
/// </summary>
public static class VersionComparer
{
    /// <summary>Returns true when <paramref name="latest"/> is newer than <paramref name="installed"/>.</summary>
    public static bool IsNewer(string? installed, string? latest)
    {
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest))
        {
            return false;
        }

        installed = installed.Split(',')[0];
        latest = latest.Split(',')[0];
        return Compare(installed, latest) < 0;
    }

    /// <summary>Compares two version strings. Returns negative if a &lt; b, 0 if equal, positive if a &gt; b.</summary>
    public static int Compare(string a, string b)
    {
        a = Normalize(a);
        b = Normalize(b);

        if (a == b)
        {
            return 0;
        }

        // Try SemVer first — it handles pre-release suffixes like -beta.1 that
        // System.Version would silently strip, causing "1.0.0-beta" == "1.0.0".
        if (TryParseSemVer(a, out var sa) && TryParseSemVer(b, out var sb))
        {
            return CompareSemVer(sa, sb);
        }

        // Fall back to System.Version for 4-part numeric versions (x.y.z.w)
        if (TryParseVersion(a, out var va) && TryParseVersion(b, out var vb))
        {
            return va.CompareTo(vb);
        }

        // Try date-based (YYYYMMDD digits only)
        if (TryParseDate(a, out var da) && TryParseDate(b, out var db))
        {
            return da.CompareTo(db);
        }

        // Lexicographic fallback
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string v)
    {
        // Strip common leading 'v' or 'V' prefix (e.g. "v1.2.3" → "1.2.3")
        v = v.Trim();
        if (v.Length > 0 && (v[0] == 'v' || v[0] == 'V'))
        {
            v = v[1..];
        }

        return v;
    }

    private static bool TryParseVersion(string v, out Version result)
    {
        // Only attempt if all characters before any '-' are numeric dots
        var core = v.Split('-', 2)[0];
        return Version.TryParse(core, out result!);
    }

    private static bool TryParseSemVer(string v, out SemVer result)
    {
        result = default;
        // SemVer: MAJOR.MINOR.PATCH[-pre][+build]
        var plusIdx = v.IndexOf('+');
        var noMeta = plusIdx >= 0 ? v[..plusIdx] : v;
        var dashIdx = noMeta.IndexOf('-');
        var corePart = dashIdx >= 0 ? noMeta[..dashIdx] : noMeta;
        var prePart = dashIdx >= 0 ? noMeta[(dashIdx + 1)..] : null;

        var parts = corePart.Split('.');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], out var major)) return false;
        if (!int.TryParse(parts[1], out var minor)) return false;
        var patch = 0;
        if (parts.Length >= 3 && !int.TryParse(parts[2], out patch)) return false;

        result = new SemVer(major, minor, patch, prePart);
        return true;
    }

    private static int CompareSemVer(SemVer a, SemVer b)
    {
        var c = a.Major.CompareTo(b.Major);
        if (c != 0) return c;
        c = a.Minor.CompareTo(b.Minor);
        if (c != 0) return c;
        c = a.Patch.CompareTo(b.Patch);
        if (c != 0) return c;

        // Pre-release rules: no pre-release > pre-release (1.0.0 > 1.0.0-alpha)
        if (a.PreRelease is null && b.PreRelease is not null) return 1;
        if (a.PreRelease is not null && b.PreRelease is null) return -1;
        if (a.PreRelease is null && b.PreRelease is null) return 0;

        return string.Compare(a.PreRelease, b.PreRelease, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseDate(string v, out int yyyymmdd)
    {
        yyyymmdd = 0;
        // Compact: 20240101
        if (v.Length == 8 && int.TryParse(v, out yyyymmdd))
        {
            return yyyymmdd is >= 19800101 and <= 21001231;
        }

        // Dotted: 2024.01.01
        var parts = v.Split('.');
        if (parts.Length == 3
            && parts[0].Length == 4
            && int.TryParse(parts[0], out var y)
            && int.TryParse(parts[1], out var m)
            && int.TryParse(parts[2], out var d))
        {
            yyyymmdd = (y * 10000) + (m * 100) + d;
            return y is >= 1980 and <= 2100;
        }

        return false;
    }

    private readonly record struct SemVer(int Major, int Minor, int Patch, string? PreRelease);
    
    public static readonly IComparer<string> Instance = Comparer<string>.Create(Compare);
}