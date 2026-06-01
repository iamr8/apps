namespace apps.Components.Audit;

public static class CvssV3Calculator
{
    // Metric weights per CVSS v3.1 specification, Section 7.4
    private static readonly Dictionary<string, double> AttackVector = new()
    {
        ["N"] = 0.85, // Network
        ["A"] = 0.62, // Adjacent
        ["L"] = 0.55, // Local
        ["P"] = 0.20 // Physical
    };

    private static readonly Dictionary<string, double> AttackComplexity = new()
    {
        ["L"] = 0.77, // Low
        ["H"] = 0.44 // High
    };

    // Privileges Required has two value sets depending on Scope
    private static readonly Dictionary<string, double> PrivilegesRequiredUnchanged = new()
    {
        ["N"] = 0.85,
        ["L"] = 0.62,
        ["H"] = 0.27
    };

    private static readonly Dictionary<string, double> PrivilegesRequiredChanged = new()
    {
        ["N"] = 0.85,
        ["L"] = 0.68,
        ["H"] = 0.50
    };

    private static readonly Dictionary<string, double> UserInteraction = new()
    {
        ["N"] = 0.85, // None
        ["R"] = 0.62 // Required
    };

    private static readonly Dictionary<string, double> ImpactMetric = new()
    {
        ["N"] = 0.00, // None
        ["L"] = 0.22, // Low
        ["H"] = 0.56 // High
    };

    public static double GetSeverityScore(string vector)
    {
        if (string.IsNullOrWhiteSpace(vector))
            throw new ArgumentException("Vector cannot be null or empty.", nameof(vector));

        var metrics = ParseVector(vector);

        // Required base metrics
        string av = Require(metrics, "AV");
        string ac = Require(metrics, "AC");
        string pr = Require(metrics, "PR");
        string ui = Require(metrics, "UI");
        string s = Require(metrics, "S");
        string c = Require(metrics, "C");
        string i = Require(metrics, "I");
        string a = Require(metrics, "A");

        bool scopeChanged = s == "C";

        double avW = Lookup(AttackVector, av, "AV");
        double acW = Lookup(AttackComplexity, ac, "AC");
        double prW = Lookup(scopeChanged ? PrivilegesRequiredChanged : PrivilegesRequiredUnchanged, pr, "PR");
        double uiW = Lookup(UserInteraction, ui, "UI");
        double cW = Lookup(ImpactMetric, c, "C");
        double iW = Lookup(ImpactMetric, i, "I");
        double aW = Lookup(ImpactMetric, a, "A");

        // Impact Sub-Score
        double iss = 1.0 - ((1.0 - cW) * (1.0 - iW) * (1.0 - aW));

        // Impact
        double impact = scopeChanged
            ? 7.52 * (iss - 0.029) - 3.25 * Math.Pow(iss - 0.02, 15)
            : 6.42 * iss;

        // Exploitability
        double exploitability = 8.22 * avW * acW * prW * uiW;

        // Base Score
        double baseScore;
        if (impact <= 0)
        {
            baseScore = 0.0;
        }
        else
        {
            double raw = scopeChanged
                ? 1.08 * (impact + exploitability)
                : impact + exploitability;

            baseScore = RoundUp(Math.Min(raw, 10.0));
        }

        return baseScore;
    }

    private static Dictionary<string, string> ParseVector(string vector)
    {
        var result = new Dictionary<string, string>();
        var parts = vector.Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var kv = part.Split(':');
            if (kv.Length != 2)
                throw new FormatException($"Invalid metric segment: '{part}'");

            string key = kv[0].Trim();
            string value = kv[1].Trim();

            // Skip the version prefix (e.g. "CVSS:3.1")
            if (key.Equals("CVSS", StringComparison.OrdinalIgnoreCase))
            {
                if (value != "3.0" && value != "3.1")
                    throw new NotSupportedException($"Only CVSS v3.0 and v3.1 are supported. Got: {value}");
                continue;
            }

            result[key] = value;
        }

        return result;
    }

    private static string Require(Dictionary<string, string> metrics, string key)
    {
        if (!metrics.TryGetValue(key, out var value))
            throw new FormatException($"Required metric '{key}' is missing from vector.");
        return value;
    }

    private static double Lookup(Dictionary<string, double> table, string value, string metricName)
    {
        if (!table.TryGetValue(value, out var weight))
            throw new FormatException($"Invalid value '{value}' for metric '{metricName}'.");
        return weight;
    }

    // CVSS v3.1 spec, Section 7.1 — round up to one decimal place
    private static double RoundUp(double input)
    {
        int scaled = (int)Math.Round(input * 100000.0);
        if (scaled % 10000 == 0)
            return scaled / 100000.0;
        return (Math.Floor(scaled / 10000.0) + 1) / 10.0;
    }
}