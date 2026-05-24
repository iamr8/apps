using System.Runtime.CompilerServices;
using System.Xml;

using apps.Infrastructure;
using apps.Checkers;
using apps.Models;

using Microsoft.Extensions.Logging;

namespace apps.Components.Sparkle;

/// <summary>
/// Priority 4 checker — fetches the Sparkle appcast XML from the URL stored in
/// <see cref="AppRecord.UpdateMethodDetail"/> (the <c>SUFeedURL</c> read from <c>Info.plist</c>)
/// and extracts the latest compatible enclosure version.
///
/// Supports both Sparkle 1 (RSS) and Sparkle 2 (same XML schema).
/// Skips enclosures whose <c>sparkle:minimumSystemVersion</c> exceeds the running macOS version.
/// All fetches fan out concurrently via the shared <c>"sparkle"</c> named client.
/// </summary>
public sealed class SparkleChecker(IHttpClientFactory httpClientFactory, ILogger<SparkleChecker> logger)
    : IUpdateChecker
{
    // Resolved once at startup — does not change during a run.
    private static readonly Version CurrentOsVersion = Environment.OSVersion.Version;

    /// <inheritdoc/>
    public UpdateMethod Method => UpdateMethod.Sparkle;

    /// <inheritdoc/>
    public string DisplayName => "Sparkle";

    /// <inheritdoc/>
    public bool CanCheck(AppRecord app)
        => app is { UpdateMethod: UpdateMethod.Sparkle, UpdateMethodDetail: not null };

    /// <inheritdoc/>
    public async Task<UpdateCheckResult> CheckAsync(AppRecord app, CancellationToken cancellationToken = default)
        => await CheckOneAsync(app, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<UpdateCheckResult>> CheckBatchAsync(
        IReadOnlyList<AppRecord> apps,
        CancellationToken cancellationToken = default)
    {
        var tasks = apps.Select(a => CheckOneAsync(a, cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<UpdateCheckResult> CheckStreamAsync(
        IReadOnlyList<AppRecord> apps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var task in Task.WhenEach(apps.Select(a => CheckOneAsync(a, cancellationToken))).ConfigureAwait(false))
        {
            yield return await task.ConfigureAwait(false);
        }
    }

    private async Task<UpdateCheckResult> CheckOneAsync(AppRecord app, CancellationToken cancellationToken)
    {
        var feedUrl = app.UpdateMethodDetail!;

        try
        {
            using var client = httpClientFactory.CreateClient("sparkle");
            using var response = await client.GetAsync(feedUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var (latestShort, latestBuild) = FindLatestItem(xml);

            if (latestShort is null && latestBuild is null)
            {
                return Err(app, "No compatible entry found in appcast");
            }

            bool updateAvailable;
            string? displayInstalled;
            string? displayLatest;

            if (VersionComparer.IsNewer(app.InstalledVersion, latestShort))
            {
                // Marketing version is newer — straightforward update.
                updateAvailable = true;
                displayInstalled = app.InstalledVersion;
                displayLatest = latestShort;
            }
            else if (string.Equals(app.InstalledVersion, latestShort, StringComparison.OrdinalIgnoreCase)
                && app.InstalledBuildVersion is not null
                && latestBuild is not null
                && VersionComparer.IsNewer(app.InstalledBuildVersion, latestBuild))
            {
                // Same marketing version (e.g. "12.7") but a newer build number (e.g. 281567 → 281596).
                // Annotate both sides with their build numbers so the display shows what actually changed.
                updateAvailable = true;
                displayInstalled = $"{app.InstalledVersion} ({app.InstalledBuildVersion})";
                displayLatest = $"{latestShort} ({latestBuild})";
            }
            else
            {
                updateAvailable = false;
                displayInstalled = app.InstalledVersion;
                displayLatest = latestShort ?? latestBuild;
            }

            return new UpdateCheckResult(app.Name, UpdateMethod.Sparkle, updateAvailable, displayInstalled, displayLatest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Sparkle check failed for {Name} ({Url})",
                app.Name,
                feedUrl);
            return Err(app, ex.Message);
        }
    }

    /// <summary>
    /// Parses the appcast XML and returns the <c>(shortVersion, buildVersion)</c> pair from
    /// the highest compatible item.
    /// Handles both Sparkle 1 (version as <c>&lt;enclosure&gt;</c> attributes) and Sparkle 2
    /// (version as child elements of <c>&lt;item&gt;</c>).
    /// Returns <c>(null, null)</c> when no compatible item is found or parsing fails.
    /// </summary>
    private (string? ShortVersion, string? BuildVersion) FindLatestItem(string xml)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var items = doc.SelectNodes("//channel/item") ?? doc.SelectNodes("//item");
            if (items is null || items.Count == 0)
            {
                return (null, null);
            }

            return SelectBestItem(items);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to parse Sparkle appcast XML");
            return (null, null);
        }
    }

    /// <summary>
    /// Iterates <paramref name="items"/> and returns the <c>(shortVersion, buildVersion)</c>
    /// pair from the highest-versioned compatible item.
    /// </summary>
    private static (string? Short, string? Build) SelectBestItem(XmlNodeList items)
    {
        (string? Short, string? Build) best = (null, null);

        foreach (XmlNode item in items)
        {
            var candidate = TryExtractItemVersionPair(item);
            if (candidate.Short is null && candidate.Build is null)
            {
                continue;
            }

            var candidateVer = candidate.Short ?? candidate.Build;
            var bestVer = best.Short ?? best.Build;

            if (bestVer is null || (candidateVer is not null && VersionComparer.Compare(candidateVer, bestVer) > 0))
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Extracts the <c>(shortVersion, buildVersion)</c> pair from a single <c>&lt;item&gt;</c> node.
    /// Returns <c>(null, null)</c> when the item is OS-incompatible or carries no version.
    /// </summary>
    private static (string? Short, string? Build) TryExtractItemVersionPair(XmlNode item)
    {
        // minimumSystemVersion can be a child element (Sparkle 2) or an enclosure attribute (Sparkle 1).
        var minOs = GetItemChildText(item, "minimumSystemVersion")
            ?? GetMainEnclosureAttr(item, "sparkle:minimumSystemVersion");

        if (minOs is not null && Version.TryParse(minOs, out var minVer) && CurrentOsVersion < minVer)
        {
            return (null, null);
        }

        // Sparkle 2 stores versions as child elements; Sparkle 1 stores them as enclosure attributes.
        var shortVer = GetItemChildText(item, "shortVersionString")
            ?? GetMainEnclosureAttr(item, "sparkle:shortVersionString");
        var buildVer = GetItemChildText(item, "version")
            ?? GetMainEnclosureAttr(item, "sparkle:version");

        if (string.IsNullOrWhiteSpace(shortVer))
        {
            shortVer = null;
        }

        if (string.IsNullOrWhiteSpace(buildVer))
        {
            buildVer = null;
        }

        return (shortVer, buildVer);
    }

    /// <summary>
    /// Returns the trimmed text of the first direct child element of <paramref name="item"/>
    /// whose <see cref="XmlNode.LocalName"/> matches <paramref name="localName"/>
    /// (namespace-prefix–agnostic).
    /// </summary>
    private static string? GetItemChildText(XmlNode item, string localName)
    {
        foreach (XmlNode child in item.ChildNodes)
        {
            if (child.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(child.InnerText))
            {
                return child.InnerText.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the value of <paramref name="attrName"/> on the first direct-child
    /// <c>&lt;enclosure&gt;</c> of <paramref name="item"/> that is not a delta enclosure
    /// (i.e. has no <c>sparkle:deltaFrom</c> attribute). Delta enclosures reside inside
    /// <c>&lt;sparkle:deltas&gt;</c> and are grandchildren of the item, so this method
    /// never reaches them via direct-child iteration.
    /// </summary>
    private static string? GetMainEnclosureAttr(XmlNode item, string attrName)
    {
        foreach (XmlNode child in item.ChildNodes)
        {
            if (child.Name != "enclosure")
            {
                continue;
            }

            if (child.Attributes?["sparkle:deltaFrom"] is not null)
            {
                continue;
            }

            var attrs = child.Attributes;
            if (attrs is null)
            {
                continue;
            }

            var val = attrs[attrName] is { } attr ? attr.Value.Trim() : null;
            if (!string.IsNullOrWhiteSpace(val))
            {
                return val;
            }
        }

        return null;
    }

    private static UpdateCheckResult Err(AppRecord app, string msg)
        => new(app.Name, UpdateMethod.Sparkle, false, app.InstalledVersion, null, msg);
}
