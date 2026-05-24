using System.Text.Json;
using System.Text.Json.Serialization;

using apps.Infrastructure;
using apps.Models;
using apps.Scanners;

using Microsoft.Extensions.Logging;

namespace apps.Orchestration;

/// <summary>
/// Stage 1.5 of the pipeline — runs between scan and check.
/// Maps every <see cref="DiscoveredApp"/> to an <see cref="AppRecord"/> and, for
/// apps that have no suggested method, probes:
/// <list type="number">
///   <item>Homebrew casks (priority 2)</item>
///   <item>Homebrew formulas (priority 3)</item>
///   <item>Chocolatey packages (priority 8), if <c>choco</c> is installed</item>
/// </list>
/// </summary>
public sealed class MethodResolverOrchestrator(
    IProcessRunner runner,
    ILogger<MethodResolverOrchestrator> logger)
{
    private static readonly string[] BrewCandidates = ["/opt/homebrew/bin/brew", "/usr/local/bin/brew"];

    /// <summary>
    /// Maps all discovered apps to <see cref="AppRecord"/> instances, resolving update methods
    /// for apps whose scanner did not suggest one. Returns all records.
    /// </summary>
    public async Task<IReadOnlyList<AppRecord>> RunAsync(
        IReadOnlyList<DiscoveredApp> discovered,
        CancellationToken cancellationToken = default)
    {
        var unresolved = discovered.Where(a => a.SuggestedMethod is null).ToArray();

        if (unresolved.Length == 0)
        {
            logger.LogDebug("All apps have a suggested method — skipping method resolver probe");
            return discovered.Select(AppRecord.From).ToArray();
        }

        logger.LogDebug("Resolving methods for {Count} unresolved app(s)", unresolved.Length);

        var (casks, formulas, brewDescriptions) = await LoadBrewInstalledAsync(cancellationToken).ConfigureAwait(false);
        var chocoPackages = await LoadChocoInstalledAsync(cancellationToken).ConfigureAwait(false);

        var resolved = new Dictionary<string, (UpdateMethod Method, string? Detail, string? Description)>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in unresolved)
        {
            var normalized = NormalizeName(app.Name);

            if (casks.TryGetValue(normalized, out var caskName))
            {
                brewDescriptions.TryGetValue(caskName, out var caskDesc);
                if (caskDesc is null)
                {
                    brewDescriptions.TryGetValue(normalized, out caskDesc);
                }

                resolved[app.Name] = (UpdateMethod.HomebrewCask, caskName, caskDesc);
                continue;
            }

            if (formulas.TryGetValue(normalized, out var formulaName))
            {
                brewDescriptions.TryGetValue(formulaName, out var formulaDesc);
                resolved[app.Name] = (UpdateMethod.HomebrewFormula, formulaName, formulaDesc);
                continue;
            }

            if (chocoPackages.TryGetValue(normalized, out var chocoName))
            {
                resolved[app.Name] = (UpdateMethod.Chocolatey, chocoName, null);
            }
        }

        // For GUI apps still unresolved, try the Homebrew cask catalog even if those apps
        // were not installed via Homebrew. brew info returns catalog version and token for each match.
        var catalogCandidates = unresolved
            .Where(a => a.Kind == AppKind.App && !resolved.ContainsKey(a.Name))
            .ToArray();

        if (catalogCandidates.Length > 0)
        {
            var catalog = await LoadBrewCatalogAsync(catalogCandidates, cancellationToken).ConfigureAwait(false);
            ApplyCatalogMatches(catalogCandidates, catalog, resolved);
            logger.LogDebug("Brew catalog: resolved {Count} additional app(s)", catalog.Count);
        }

        // Final fallback: for apps still unresolved, use `brew search --cask` for fuzzy matching
        // then validate by comparing the cask's catalog version against the installed version.
        var searchCandidates = unresolved
            .Where(a => a.Kind == AppKind.App && !resolved.ContainsKey(a.Name) && a.InstalledVersion is not null)
            .ToArray();

        if (searchCandidates.Length > 0)
        {
            var searchResults = await SearchBrewCasksAsync(searchCandidates, cancellationToken).ConfigureAwait(false);

            foreach (var (appName, method, detail, desc) in searchResults)
            {
                resolved[appName] = (method, detail, desc);
            }

            logger.LogDebug("Brew search: resolved {Count} additional app(s) via fuzzy search", searchResults.Count);
        }

        logger.LogInformation(
            "Method resolver: resolved {Resolved}/{Total} unresolved app(s) via Homebrew/Chocolatey/catalog",
            resolved.Count,
            unresolved.Length);

        return discovered.Select(app =>
        {
            var record = AppRecord.From(app);

            if (record.UpdateMethod is null && resolved.TryGetValue(app.Name, out var match))
            {
                record.UpdateMethod = match.Method;
                record.UpdateMethodDetail = match.Detail;

                if (record.Description is null && match.Description is not null)
                {
                    record.Description = match.Description;
                }
            }

            return record;
        }).ToArray();
    }

    /// <summary>
    /// Matches <paramref name="candidates"/> against the Homebrew catalog entries and
    /// populates <paramref name="resolved"/> with catalog-based <see cref="UpdateMethod.HomebrewCask"/>
    /// entries for each matched app.
    /// Only accepts a match when the catalog version is plausibly related to the installed version
    /// (prevents false positives from same-name but entirely different products).
    /// </summary>
    private void ApplyCatalogMatches(
        DiscoveredApp[] candidates,
        Dictionary<string, (string Token, string Version, string? Desc)> catalog,
        Dictionary<string, (UpdateMethod Method, string? Detail, string? Description)> resolved)
    {
        foreach (var app in candidates)
        {
            var normalized = NormalizeName(app.Name);

            if (!catalog.TryGetValue(normalized, out var catalogEntry))
            {
                continue;
            }

            if (app.InstalledVersion is not null && !VersionMatchesInstalled(app.InstalledVersion, catalogEntry.Version))
            {
                logger.LogDebug(
                    "Catalog match rejected for {App}: installed {Installed} vs catalog {Catalog} (version mismatch — likely different product)",
                    app.Name,
                    app.InstalledVersion,
                    catalogEntry.Version);
                continue;
            }

            // Detail format: "catalog:{token}:{latestVersion}" — lets the checker compare
            // without an extra subprocess call.
            resolved[app.Name] = (UpdateMethod.HomebrewCask, $"catalog:{catalogEntry.Token}:{catalogEntry.Version}", catalogEntry.Desc);
        }
    }

    /// <summary>
    /// Runs <c>brew info --json=v2 --cask</c> for each candidate app's possible token names,
    /// concurrently and individually, and returns a lookup of normalized-name → catalog entry.
    /// Tries multiple name strategies: normalized full name, .app filename from path,
    /// and individual significant words from the display name.
    /// </summary>
    private async Task<Dictionary<string, (string Token, string Version, string? Desc)>> LoadBrewCatalogAsync(
        DiscoveredApp[] candidates,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, (string Token, string Version, string? Desc)>(StringComparer.OrdinalIgnoreCase);
        var brew = BrewCandidates.FirstOrDefault(File.Exists);

        if (brew is null)
        {
            return results;
        }

        // Build a mapping: candidate normalized name → list of brew tokens to try
        var tokensTried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lookups = new List<(string CandidateKey, string Token)>();

        foreach (var app in candidates)
        {
            var candidateKey = NormalizeName(app.Name);
            var tokensForApp = GenerateCandidateTokens(app);

            foreach (var token in tokensForApp)
            {
                if (tokensTried.Add(token))
                {
                    lookups.Add((candidateKey, token));
                }
            }
        }

        if (lookups.Count == 0)
        {
            return results;
        }

        var tasks = lookups.Select(l => LookupCaskWithKeyAsync(brew, l.CandidateKey, l.Token, cancellationToken)).ToArray();
        var entries = await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (var (candidateKey, token, version, desc) in entries)
        {
            if (token is not null && version is not null && !results.ContainsKey(candidateKey))
            {
                results[candidateKey] = (token, version, desc);
            }
        }

        return results;
    }

    /// <summary>
    /// Generates multiple candidate brew cask tokens to try for a given app.
    /// Strategies: normalized full name, .app filename, individual words > 2 chars.
    /// </summary>
    private static IReadOnlyList<string> GenerateCandidateTokens(DiscoveredApp app)
    {
        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = NormalizeName(value);
            if (normalized.Length > 1 && seen.Add(normalized))
            {
                tokens.Add(normalized);
            }
        }

        // 1. Full normalized name (e.g. "jetbrains-rider")
        TryAdd(app.Name);

        // 2. App filename from path (e.g. "/Applications/Visual Studio Code.app" → "visual-studio-code")
        if (app.Path is not null)
        {
            var fileName = Path.GetFileNameWithoutExtension(app.Path);
            TryAdd(fileName);
        }

        // 3. Individual words from the name that are > 2 chars (e.g. "Rider" from "JetBrains Rider")
        var words = app.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1)
        {
            foreach (var word in words)
            {
                if (word.Length > 2)
                {
                    TryAdd(word);
                }
            }
        }

        return tokens;
    }

    /// <summary>
    /// Looks up a single cask <paramref name="token"/> and returns the result tagged with <paramref name="candidateKey"/>.
    /// </summary>
    private async Task<(string CandidateKey, string? Token, string? Version, string? Desc)> LookupCaskWithKeyAsync(
        string brew,
        string candidateKey,
        string token,
        CancellationToken cancellationToken)
    {
        var (resolvedToken, version, desc) = await LookupCaskTokenAsync(brew, token, cancellationToken).ConfigureAwait(false);
        return (candidateKey, resolvedToken, version, desc);
    }

    /// <summary>
    /// Looks up a single cask <paramref name="token"/> via <c>brew info --json=v2 --cask</c>.
    /// Returns <c>(null, null, null)</c> when the token is unknown or the subprocess fails.
    /// </summary>
    private async Task<(string? Token, string? Version, string? Desc)> LookupCaskTokenAsync(
        string brew,
        string token,
        CancellationToken cancellationToken)
    {
        ProcessResult proc;
        try
        {
            proc = await runner.RunAsync(brew, $"info --json=v2 --cask {token}", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to look up cask '{Token}'", token);
            return (null, null, null);
        }

        if (string.IsNullOrWhiteSpace(proc.StandardOutput))
        {
            return (null, null, null);
        }

        try
        {
            var root = JsonSerializer.Deserialize(proc.StandardOutput, BrewCatalogJsonContext.Default.BrewCatalogRoot);
            var cask = root?.Casks?.FirstOrDefault();

            if (string.IsNullOrWhiteSpace(cask?.Token) || string.IsNullOrWhiteSpace(cask.Version))
            {
                return (null, null, null);
            }

            return (cask.Token, cask.Version, cask.Desc);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to parse brew info JSON for cask '{Token}'", token);
            return (null, null, null);
        }
    }

    /// <summary>
    /// For apps still unresolved, runs <c>brew search --cask &lt;name&gt;</c> to find fuzzy matches,
    /// then validates each candidate by comparing its catalog version against the app's installed version.
    /// A match is confirmed when the installed version starts with or equals the cask version (ignoring build suffixes).
    /// </summary>
    private async Task<IReadOnlyList<(string AppName, UpdateMethod Method, string? Detail, string? Description)>> SearchBrewCasksAsync(
        DiscoveredApp[] candidates,
        CancellationToken cancellationToken)
    {
        var results = new List<(string AppName, UpdateMethod Method, string? Detail, string? Description)>();
        var brew = BrewCandidates.FirstOrDefault(File.Exists);

        if (brew is null)
        {
            return results;
        }

        foreach (var app in candidates)
        {
            var searchTerm = NormalizeName(app.Name).Replace("-", " ");

            ProcessResult searchResult;
            try
            {
                searchResult = await runner.RunAsync(brew, $"search --cask {searchTerm}", cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            if (!searchResult.Success || string.IsNullOrWhiteSpace(searchResult.StandardOutput))
            {
                continue;
            }

            var searchTokens = searchResult.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.StartsWith("==>", StringComparison.Ordinal))
                .SelectMany(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(t => t.Length > 1)
                .ToArray();

            if (searchTokens.Length == 0 || searchTokens.Length > 10)
            {
                continue;
            }

            // Try each search result — validate via version match
            var matched = false;

            foreach (var candidateToken in searchTokens)
            {
                if (matched)
                {
                    break;
                }

                var (resolvedToken, version, desc) = await LookupCaskTokenAsync(brew, candidateToken, cancellationToken).ConfigureAwait(false);

                if (resolvedToken is null || version is null)
                {
                    continue;
                }

                if (VersionMatchesInstalled(app.InstalledVersion!, version))
                {
                    var detail = $"catalog:{resolvedToken}:{version}";
                    results.Add((app.Name, UpdateMethod.HomebrewCask, detail, desc));
                    matched = true;
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Returns <c>true</c> when the installed version plausibly matches the cask catalog version.
    /// Handles cases where the cask version has a build suffix (e.g. "1.8.0,abc123") or the installed
    /// version is a prefix of the catalog version.
    /// </summary>
    private static bool VersionMatchesInstalled(string installed, string catalogVersion)
    {
        // Strip brew-style build suffixes: "1.8.0,abc123" → "1.8.0"
        var commaIdx = catalogVersion.IndexOf(',');
        var cleanCatalog = commaIdx > 0 ? catalogVersion[..commaIdx] : catalogVersion;

        if (string.Equals(installed, cleanCatalog, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Installed might be a prefix (e.g. "1.8" matches "1.8.0")
        if (cleanCatalog.StartsWith(installed, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (installed.StartsWith(cleanCatalog, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private async Task<(Dictionary<string, string> Casks, Dictionary<string, string> Formulas, Dictionary<string, string> Descriptions)> LoadBrewInstalledAsync(CancellationToken cancellationToken)
    {
        var casks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var formulas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var brew = BrewCandidates.FirstOrDefault(File.Exists);

        if (brew is null)
        {
            logger.LogDebug("brew not found — skipping Homebrew resolution");
            return (casks, formulas, descriptions);
        }

        var caskTask = runner.RunAsync(brew, "list --cask --versions", cancellationToken);
        var formulaTask = runner.RunAsync(brew, "list --versions", cancellationToken);
        var infoTask = runner.RunAsync(brew, "info --json=v2 --installed", cancellationToken);

        var caskResult = await caskTask.ConfigureAwait(false);
        var formulaResult = await formulaTask.ConfigureAwait(false);
        var infoResult = await infoTask.ConfigureAwait(false);

        if (infoResult.Success && !string.IsNullOrWhiteSpace(infoResult.StandardOutput))
        {
            try
            {
                var info = JsonSerializer.Deserialize(infoResult.StandardOutput, apps.Components.Homebrew.HomebrewJsonContext.Default.BrewInfoRoot);

                if (info?.Formulae is not null)
                {
                    foreach (var f in info.Formulae)
                    {
                        if (!string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.Desc))
                        {
                            descriptions[f.Name] = f.Desc;
                        }
                    }
                }

                if (info?.Casks is not null)
                {
                    foreach (var c in info.Casks)
                    {
                        if (string.IsNullOrWhiteSpace(c.Token))
                        {
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(c.Desc))
                        {
                            descriptions[c.Token] = c.Desc;

                            var displayName = c.Name?.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                            if (!string.IsNullOrWhiteSpace(displayName))
                            {
                                descriptions[NormalizeName(displayName)] = c.Desc;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to parse 'brew info --json=v2 --installed' in method resolver");
            }
        }

        if (caskResult.Success)
        {
            foreach (var line in SplitLines(caskResult.StandardOutput))
            {
                var name = FirstToken(line);

                if (!string.IsNullOrWhiteSpace(name))
                {
                    casks[NormalizeName(name)] = name;
                }
            }
        }
        else
        {
            logger.LogDebug("'brew list --cask --versions' failed: {Err}", caskResult.StandardError.Trim());
        }

        if (formulaResult.Success)
        {
            foreach (var line in SplitLines(formulaResult.StandardOutput))
            {
                var name = FirstToken(line);

                if (!string.IsNullOrWhiteSpace(name) && !casks.ContainsKey(NormalizeName(name)))
                {
                    formulas[NormalizeName(name)] = name;
                }
            }
        }
        else
        {
            logger.LogDebug("'brew list --versions' failed: {Err}", formulaResult.StandardError.Trim());
        }

        logger.LogDebug(
            "Brew installed: {CaskCount} cask(s), {FormulaCount} formula(e), {DescCount} description(s) indexed",
            casks.Count,
            formulas.Count,
            descriptions.Count);

        return (casks, formulas, descriptions);
    }

    private async Task<Dictionary<string, string>> LoadChocoInstalledAsync(CancellationToken cancellationToken)
    {
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var choco = ScannerHelper.FindExecutable("choco");

        if (choco is null)
        {
            return packages;
        }

        ProcessResult result;
        try
        {
            result = await runner.RunAsync(choco, "list", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to run 'choco list'");
            return packages;
        }

        if (!result.Success)
        {
            logger.LogDebug("'choco list' failed: {Err}", result.StandardError.Trim());
            return packages;
        }

        foreach (var line in SplitLines(result.StandardOutput))
        {
            if (IsChocoHeaderLine(line))
            {
                continue;
            }

            var name = FirstToken(line);

            if (!string.IsNullOrWhiteSpace(name))
            {
                packages[NormalizeName(name)] = name;
            }
        }

        logger.LogDebug("Chocolatey installed: {Count} package(s) indexed", packages.Count);
        return packages;
    }

    private static string NormalizeName(string name)
    {
        // "Visual Studio Code" → "visual-studio-code", "1Password" → "1password"
        return string.Create(name.Length, name, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                span[i] = c switch
                {
                    ' ' or '_' => '-',
                    _ => char.ToLowerInvariant(c)
                };
            }
        }).TrimEnd('-');
    }

    private static string FirstToken(string line)
    {
        var space = line.IndexOf(' ');
        return space > 0 ? line[..space] : line;
    }

    private static string[] SplitLines(string output)
    {
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsChocoHeaderLine(string line)
    {
        return line.StartsWith("Chocolatey", StringComparison.OrdinalIgnoreCase)
            || line.Contains("packages installed", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Minimal projection of the <c>brew info --json=v2 --cask</c> response root.</summary>
internal sealed class BrewCatalogRoot
{
    [JsonPropertyName("casks")]
    public BrewCatalogCask[]? Casks { get; init; }
}

/// <summary>Per-cask entry from <c>brew info --json=v2 --cask</c>.</summary>
internal sealed class BrewCatalogCask
{
    /// <summary>Homebrew cask token (e.g. <c>"claude"</c>).</summary>
    [JsonPropertyName("token")]
    public string? Token { get; init; }

    /// <summary>Array of human-readable display names (e.g. ["Claude"]).</summary>
    [JsonPropertyName("name")]
    public string[]? Name { get; init; }

    /// <summary>Short description of the cask (e.g. "Anthropic's official Claude AI desktop app").</summary>
    [JsonPropertyName("desc")]
    public string? Desc { get; init; }

    /// <summary>
    /// Latest catalog version string (e.g. <c>"1.8555.2,a476c316..."</c>).
    /// Casks that pin a commit hash append it after a comma; the checker strips the suffix.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

/// <summary>Source-generated JSON context for <see cref="apps.Orchestration.BrewCatalogJsonContext.BrewCatalogRoot"/> (AOT-safe).</summary>
[JsonSerializable(typeof(BrewCatalogRoot))]
internal sealed partial class BrewCatalogJsonContext : JsonSerializerContext;

