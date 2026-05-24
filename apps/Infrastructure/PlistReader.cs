using System.Diagnostics;
using System.Xml;

using Microsoft.Extensions.Logging;

namespace apps.Infrastructure;

/// <summary>
/// Reads Info.plist files from macOS .app bundles.
/// Handles both XML plists (most modern apps) and binary plists (converted via plutil).
/// </summary>
public sealed class PlistReader(ILogger<PlistReader> logger)
{
    // Signature of Apple's binary plist format
    private static readonly byte[] BinaryPlistMagic = "bplist00"u8.ToArray();

    /// <summary>
    /// Reads and parses the Info.plist for the given .app bundle path.
    /// Returns null if the plist cannot be found or parsed.
    /// </summary>
    public async Task<PlistInfo?> ReadAsync(string appBundlePath, CancellationToken cancellationToken = default)
    {
        var plistPath = Path.Combine(appBundlePath, "Contents", "Info.plist");
        if (!File.Exists(plistPath))
        {
            return null;
        }

        try
        {
            var xml = await ReadAsXmlAsync(plistPath, cancellationToken);
            if (xml is null)
            {
                return null;
            }

            return ParseXmlPlist(xml);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to parse Info.plist at {Path}", plistPath);
            return null;
        }
    }

    private async Task<string?> ReadAsXmlAsync(string plistPath, CancellationToken cancellationToken)
    {
        // Peek at the first 8 bytes to detect binary plist
        await using var fs = File.OpenRead(plistPath);
        var header = new byte[8];
        var read = await fs.ReadAsync(header, cancellationToken);

        if (read >= 8 && header.AsSpan().SequenceEqual(BinaryPlistMagic))
        {
            // Binary plist: convert to XML via plutil (always available on macOS 10+)
            logger.LogDebug("Binary plist detected, converting via plutil: {Path}", plistPath);
            return await ConvertBinaryPlistAsync(plistPath, cancellationToken);
        }

        // XML plist: read directly
        fs.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task<string?> ConvertBinaryPlistAsync(string plistPath, CancellationToken cancellationToken)
    {
        // plutil -convert xml1 <file> -o -  →  prints XML to stdout
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/plutil",
            Arguments = $"-convert xml1 \"{plistPath}\" -o -",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        proc.Start();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken);

        if (proc.ExitCode != 0)
            return null;

        return await stdoutTask;
    }

    private static PlistInfo ParseXmlPlist(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        // Apple XML plist schema: <plist><dict><key>…</key><string>…</string>…</dict></plist>
        var dict = doc.SelectSingleNode("/plist/dict") ?? throw new FormatException("No top-level <dict> in plist");

        var pairs = ExtractKeyValuePairs(dict);

        var displayName = GetString(pairs, "CFBundleDisplayName") ?? GetString(pairs, "CFBundleName");
        var shortVersion = GetString(pairs, "CFBundleShortVersionString");
        var bundleVersion = GetString(pairs, "CFBundleVersion");
        var bundleId = GetString(pairs, "CFBundleIdentifier");
        var sparkleUrl = GetString(pairs, "SUFeedURL");
        var hasSparkleKey = pairs.ContainsKey("SUPublicEDKey") || pairs.ContainsKey("SUPublicDSAKeyFile");

        string? nsExtPointId = null;
        var nsExtDict = FindChildDict(dict, "NSExtension");
        if (nsExtDict is not null)
        {
            var nsExtPairs = ExtractKeyValuePairs(nsExtDict);
            nsExtPointId = GetString(nsExtPairs, "NSExtensionPointIdentifier");
        }

        return new PlistInfo(displayName, shortVersion, bundleVersion, bundleId, sparkleUrl, hasSparkleKey, nsExtPointId);
    }

    private static Dictionary<string, string?> ExtractKeyValuePairs(XmlNode dict)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        var nodes = dict.ChildNodes;
        var i = 0;

        while (i < nodes.Count)
        {
            var keyNode = nodes[i];
            if (keyNode?.Name != "key")
            {
                i++;
                continue;
            }

            var key = keyNode.InnerText;
            var valueNode = nodes[i + 1];

            if (valueNode is not null)
            {
                result[key] = valueNode.Name switch
                {
                    "string" => valueNode.InnerText,
                    "true" => "true",
                    "false" => "false",
                    "integer" or "real" => valueNode.InnerText,
                    _ => null
                };
            }

            i += 2;
        }

        return result;
    }

    private static string? GetString(Dictionary<string, string?> pairs, string key)
    {
        return pairs.GetValueOrDefault(key);
    }

    /// <summary>
    /// Finds the child <c>&lt;dict&gt;</c> node that immediately follows a <c>&lt;key&gt;</c>
    /// node with the given name inside <paramref name="dict"/>.
    /// Returns <see langword="null"/> when the key is absent or its value is not a <c>&lt;dict&gt;</c>.
    /// </summary>
    private static XmlNode? FindChildDict(XmlNode dict, string key)
    {
        var nodes = dict.ChildNodes;
        var i = 0;

        while (i < nodes.Count)
        {
            var keyNode = nodes[i];
            if (keyNode?.Name == "key" && keyNode.InnerText == key && i + 1 < nodes.Count)
            {
                var valueNode = nodes[i + 1];
                return valueNode?.Name == "dict" ? valueNode : null;
            }

            i++;
        }

        return null;
    }
}