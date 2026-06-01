using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Xml;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

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
    /// Handles both standard macOS bundles (<c>Contents/Info.plist</c>) and
    /// iOS apps running on Apple Silicon (<c>Wrapper/*.app/Info.plist</c>).
    /// Returns null if the plist cannot be found or parsed.
    /// </summary>
    public async Task<PlistInfo?> ReadAsync(string appBundlePath, CancellationToken cancellationToken = default)
    {
        var plistPath = Path.Combine(appBundlePath, "Contents", "Info.plist");
        if (!File.Exists(plistPath))
        {
            plistPath = FindWrappedPlist(appBundlePath);
            if (plistPath is null)
            {
                return null;
            }
        }

        try
        {
            var xml = await ReadAsXmlAsync(plistPath, cancellationToken);
            if (xml is null)
            {
                return null;
            }

            return ParseXmlPlist(appBundlePath, xml);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to parse Info.plist at {Path}", plistPath);
            return null;
        }
    }

    /// <summary>
    /// Locates the Info.plist inside an iOS app wrapper structure used by Apple Silicon Macs.
    /// iOS apps from the App Store are installed as <c>Foo.app/Wrapper/Bar.app/Info.plist</c>.
    /// Returns <see langword="null"/> when no wrapper is found.
    /// </summary>
    private static string? FindWrappedPlist(string appBundlePath)
    {
        var wrapperDir = Path.Combine(appBundlePath, "Wrapper");
        if (!Directory.Exists(wrapperDir))
        {
            return null;
        }

        try
        {
            foreach (var innerApp in Directory.EnumerateDirectories(wrapperDir, "*.app", SearchOption.TopDirectoryOnly))
            {
                var candidate = Path.Combine(innerApp, "Info.plist");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // Permission or I/O error — fall through
        }

        return null;
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

    private static PlistInfo ParseXmlPlist(string appBundlePath, string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        // Apple XML plist schema: <plist><dict><key>…</key><string>…</string>…</dict></plist>
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var dict = doc.SelectSingleNode("/plist") ?? throw new FormatException("No top-level <dict> in plist");
        var plist = Plist.Parse(dict);

        var displayName = plist.TryGetValue("CFBundleDisplayName", out var bdn)
            ? bdn.GetString()
            : plist.TryGetValue("CFBundleName", out var bn)
                ? bn.GetString()
                : null;
        var shortVersion = plist.GetString("CFBundleShortVersionString");
        var bundleVersion = plist.GetString("CFBundleVersion");
        var bundleId = plist.GetString("CFBundleIdentifier");
        var sparkleUrl = plist.GetString("SUFeedURL");
        var isElectronApp = plist.ContainsKey("ElectronAsarIntegrity");
        var googleKeystoneUrl = plist.GetString("KSUpdateURL");
        var isSafariExtension = false;
        if (plist.TryGetValue("NSExtension", out var ext))
        {
            var nsExtPointId = ext.GetString("NSExtensionPointIdentifier");
            if (nsExtPointId is "com.apple.Safari.extension" or "com.apple.Safari.web-extension")
            {
                isSafariExtension = true;
            }
        }
        else
        {
            isSafariExtension = plist.ContainsKey("SFSafariWebExtensionConverterVersion");
        }
        
        // TODO: check if built by DevMate
        // TODO: check if iOS App Bundle

        return new PlistInfo(displayName, shortVersion, bundleVersion, bundleId, sparkleUrl, googleKeystoneUrl, isSafariExtension, isElectronApp, plist);
    }

    [DebuggerDisplay("{GetDebuggerDisplay(),nq}")]
    public class Plist : IEnumerable<KeyValuePair<string, Plist>>
    {
        private readonly Dictionary<string, Plist>? _dictionary;
        private readonly List<Plist>? _array;
        private readonly decimal? _numberValue;
        private readonly bool? _booleanValue;
        private readonly string? _stringValue;

        [MemberNotNullWhen(true, nameof(_array))]
        private bool IsArray { get; }

        [MemberNotNullWhen(true, nameof(_dictionary))]
        private bool IsDictionary { get; }

        [MemberNotNullWhen(true, nameof(_stringValue))]
        private bool IsString { get; }

        [MemberNotNullWhen(true, nameof(_numberValue))]
        private bool IsNumber { get; }

        [MemberNotNullWhen(true, nameof(_booleanValue))]
        private bool IsBoolean { get; }

        private Plist(Dictionary<string, Plist> dictionary)
        {
            _dictionary = dictionary;
            IsDictionary = true;
        }

        private Plist(List<Plist> array)
        {
            _array = array;
            IsArray = true;
        }

        private Plist(string @string)
        {
            _stringValue = @string;
            IsString = true;
        }

        private Plist(bool value)
        {
            _booleanValue = value;
            IsBoolean = true;
        }

        private Plist(decimal value)
        {
            _numberValue = value;
            IsNumber = true;
        }

        /// <summary>
        /// Parses an XML node into a structured <see cref="Plist"/> tree.
        /// Supports <c>&lt;plist&gt;</c>, <c>&lt;dict&gt;</c>, <c>&lt;array&gt;</c>,
        /// <c>&lt;string&gt;</c>, <c>&lt;integer&gt;</c>, <c>&lt;real&gt;</c>,
        /// <c>&lt;true/&gt;</c>, and <c>&lt;false/&gt;</c> elements.
        /// </summary>
        public static Plist? Parse(XmlNode node)
        {
            try
            {
                return ParseNode(node) as Plist;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to parse plist XML: {e}");
                return null;
            }
        }

        public string? GetString(string? key = null)
        {
            if (key is not null && _dictionary is not null)
            {
                return _dictionary.TryGetValue(key, out var value) ? value.GetString() : null;
            }

            return IsString ? _stringValue : throw new InvalidOperationException("Plist value is not a string");
        }

        public decimal GetNumber(string? key = null)
        {
            if (key is not null && _dictionary is not null)
            {
                return _dictionary.TryGetValue(key, out var value) ? value.GetNumber() : throw new KeyNotFoundException($"Key not found: {key}");
            }

            return IsNumber ? _numberValue.Value : throw new InvalidOperationException("Plist value is not a number");
        }

        public bool GetBoolean(string? key = null)
        {
            if (key is not null && _dictionary is not null)
            {
                return _dictionary.TryGetValue(key, out var value) ? value.GetBoolean() : throw new KeyNotFoundException($"Key not found: {key}");
            }

            return IsBoolean ? _booleanValue.Value : throw new InvalidOperationException("Plist value is not a boolean");
        }

        /// <summary>
        /// Gets the number of elements in the array, or 0 if not an array.
        /// </summary>
        public int Count => _array?.Count ?? _dictionary?.Count ?? 0;

        /// <summary>
        /// Attempts to get the value associated with the specified key.
        /// </summary>
        public bool TryGetValue(string key, out Plist? value)
        {
            if (_dictionary is not null)
            {
                return _dictionary.TryGetValue(key, out value);
            }

            value = null;
            return false;
        }

        /// <summary>
        /// Returns whether the dictionary contains the specified key.
        /// </summary>
        public bool ContainsKey(string key) => _dictionary?.ContainsKey(key) ?? false;

        public IEnumerator<KeyValuePair<string, Plist>> GetEnumerator()
        {
            return (_dictionary ?? new Dictionary<string, Plist>()).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private static Plist ParseNode(XmlNode node)
        {
            return node.Name switch
            {
                "plist" => ParsePlistRoot(node),
                "dict" => ParseDict(node),
                "array" => ParseArray(node),
                "string" => new Plist(node.InnerText),
                "true" => new Plist(true),
                "false" => new Plist(false),
                "integer" or "real" => new Plist(decimal.Parse(node.InnerText)),
                "data" or "date" => new Plist(node.InnerText),
                _ => throw new FormatException($"Unsupported plist value type: <{node.Name}>")
            };
        }

        private static Plist ParsePlistRoot(XmlNode plistNode)
        {
            foreach (XmlNode child in plistNode.ChildNodes)
            {
                if (child.Name is "dict" or "array")
                {
                    return (Plist)ParseNode(child);
                }
            }

            throw new FormatException("No <dict> or <array> found inside <plist>");
        }

        private static Plist ParseDict(XmlNode dictNode)
        {
            var output = new Dictionary<string, Plist>(StringComparer.Ordinal);
            var nodes = dictNode.ChildNodes;
            var i = 0;

            while (i < nodes.Count)
            {
                var keyNode = nodes[i];
                if (keyNode is null)
                {
                    break;
                }

                if (keyNode.Name != "key")
                {
                    i++;
                    continue;
                }

                var key = keyNode.InnerText;
                i++;

                if (i >= nodes.Count)
                {
                    break;
                }

                var valueNode = nodes[i];
                if (valueNode is null)
                {
                    break;
                }

                output[key] = ParseNode(valueNode);
                i++;
            }

            return new Plist(output);
        }

        private static Plist ParseArray(XmlNode arrayNode)
        {
            var items = new List<Plist>();

            foreach (XmlNode child in arrayNode.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element)
                {
                    items.Add(ParseNode(child));
                }
            }

            return new Plist(items);
        }

        public string GetDebuggerDisplay()
        {
            if (this.IsDictionary)
            {
                return $"dict with {this.Count} entries";
            }

            if (this.IsArray)
            {
                return $"array with {this.Count} items";
            }

            if (this.IsString)
            {
                return $"{this._stringValue}";
            }

            if (this.IsNumber)
            {
                return $"{this._numberValue}";
            }

            if (this.IsBoolean)
            {
                return $"{this._booleanValue}";
            }

            return "null";
        }
    }
}