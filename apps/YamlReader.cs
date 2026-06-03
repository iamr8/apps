using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace apps;

/// <summary>
/// Lightweight, AOT-safe YAML reader. Parses a practical subset of YAML 1.2 line-by-line
/// without any reflection-based library, making it fully compatible with Native AOT.
/// Supports block and flow mappings/sequences, all scalar types (string, number, boolean, null),
/// block scalars (<c>|</c> / <c>&gt;</c>), quoted strings, and <c>#</c> comments.
/// </summary>
public static class YamlReader
{
    /// <summary>Parses a YAML string into a <see cref="Yaml"/> document tree.</summary>
    public static Yaml? Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        return Yaml.ParseContent(content);
    }

    /// <summary>Reads and parses a YAML file asynchronously into a <see cref="Yaml"/> tree.</summary>
    public static async Task<Yaml?> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        var content = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        return Parse(content);
    }

    /// <summary>
    /// Represents a parsed YAML value: a mapping, sequence, or scalar
    /// (string, number, boolean, or null).
    /// </summary>
    [DebuggerDisplay("{GetDebuggerDisplay(),nq}")]
    public sealed class Yaml : IEnumerable<KeyValuePair<string, Yaml>>
    {
        private readonly Dictionary<string, Yaml>? _dictionary;
        private readonly List<Yaml>? _array;
        private readonly string? _stringValue;
        private readonly decimal? _numberValue;
        private readonly bool? _booleanValue;

        [MemberNotNullWhen(true, nameof(_dictionary))]
        private bool IsDictionary { get; }

        [MemberNotNullWhen(true, nameof(_array))]
        private bool IsArray { get; }

        [MemberNotNullWhen(true, nameof(_stringValue))]
        private bool IsString { get; }

        [MemberNotNullWhen(true, nameof(_numberValue))]
        private bool IsNumber { get; }

        [MemberNotNullWhen(true, nameof(_booleanValue))]
        private bool IsBoolean { get; }

        private Yaml(Dictionary<string, Yaml> dictionary)
        {
            _dictionary = dictionary;
            IsDictionary = true;
        }

        private Yaml(List<Yaml> array)
        {
            _array = array;
            IsArray = true;
        }

        private Yaml(string value)
        {
            _stringValue = value;
            IsString = true;
        }

        private Yaml(decimal value)
        {
            _numberValue = value;
            IsNumber = true;
        }

        private Yaml(bool value)
        {
            _booleanValue = value;
            IsBoolean = true;
        }

        // Sentinel null node — all Is* properties remain false
        private Yaml() { }

        /// <summary>Gets a value indicating whether this node is null or missing.</summary>
        public bool IsNull => !IsDictionary && !IsArray && !IsString && !IsNumber && !IsBoolean;

        /// <summary>Gets a value indicating whether this node is a mapping (dictionary).</summary>
        public bool IsMapping => IsDictionary;

        /// <summary>Gets a value indicating whether this node is a sequence (list).</summary>
        public bool IsSequence => IsArray;

        /// <summary>Gets the number of elements in a mapping or sequence; 0 for scalars.</summary>
        public int Count => _array?.Count ?? _dictionary?.Count ?? 0;

        internal static Yaml Null { get; } = new();
        internal static Yaml True { get; } = new(true);
        internal static Yaml False { get; } = new(false);

        internal static Yaml FromDictionary(Dictionary<string, Yaml> dict) => new(dict);
        internal static Yaml FromList(List<Yaml> list) => new(list);
        internal static Yaml FromString(string value) => new(value);
        internal static Yaml FromNumber(decimal value) => new(value);

        /// <summary>
        /// Returns the string value of this node, or the string value of the child at
        /// <paramref name="key"/> if this is a mapping. Returns <see langword="null"/>
        /// when the key is absent or the node is not a string.
        /// </summary>
        public string? GetString(string? key = null)
        {
            if (key is not null && _dictionary is not null)
            {
                return _dictionary.TryGetValue(key, out var child) ? child.GetString() : null;
            }

            return IsString ? _stringValue : throw new InvalidOperationException("YAML node is not a string");
        }

        /// <summary>
        /// Returns the numeric value of this node, or the number at <paramref name="key"/>
        /// if this is a mapping.
        /// </summary>
        public decimal GetNumber(string? key = null)
        {
            if (key is not null)
            {
                if (_dictionary is not null && _dictionary.TryGetValue(key, out var child))
                {
                    return child.GetNumber();
                }

                throw new KeyNotFoundException($"Key not found: {key}");
            }

            return IsNumber ? _numberValue!.Value : throw new InvalidOperationException("YAML node is not a number");
        }

        /// <summary>
        /// Returns the boolean value of this node, or the boolean at <paramref name="key"/>
        /// if this is a mapping.
        /// </summary>
        public bool GetBoolean(string? key = null)
        {
            if (key is not null)
            {
                if (_dictionary is not null && _dictionary.TryGetValue(key, out var child))
                {
                    return child.GetBoolean();
                }

                throw new KeyNotFoundException($"Key not found: {key}");
            }

            return IsBoolean ? _booleanValue!.Value : throw new InvalidOperationException("YAML node is not a boolean");
        }

        /// <summary>Returns the sequence item at <paramref name="index"/>, or <see langword="null"/> if out of range.</summary>
        public Yaml? GetItem(int index)
        {
            if (_array is null || (uint)index >= (uint)_array.Count)
            {
                return null;
            }

            return _array[index];
        }

        /// <summary>Returns all items in a sequence node; empty enumerable for non-sequences.</summary>
        public IEnumerable<Yaml> Items() => (IEnumerable<Yaml>?)_array ?? [];

        /// <summary>Gets the value associated with the specified mapping key.</summary>
        public bool TryGetValue(string key, [NotNullWhen(true)] out Yaml? value)
        {
            if (_dictionary is not null)
            {
                return _dictionary.TryGetValue(key, out value);
            }

            value = null;
            return false;
        }

        /// <summary>Returns whether the mapping contains the given key.</summary>
        public bool ContainsKey(string key) => _dictionary?.ContainsKey(key) ?? false;

        /// <inheritdoc/>
        public IEnumerator<KeyValuePair<string, Yaml>> GetEnumerator()
            => (_dictionary ?? new Dictionary<string, Yaml>()).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>Parses a YAML document string into a <see cref="Yaml"/> tree.</summary>
        internal static Yaml ParseContent(string content)
            => new YamlParser(content.Split('\n')).ParseDocument();

        private string GetDebuggerDisplay()
        {
            if (IsDictionary) return $"mapping({_dictionary!.Count} keys)";
            if (IsArray) return $"sequence({_array!.Count} items)";
            if (IsString) return $"\"{_stringValue}\"";
            if (IsNumber) return _numberValue!.Value.ToString(CultureInfo.InvariantCulture);
            if (IsBoolean) return _booleanValue!.Value ? "true" : "false";
            return "null";
        }
    }

    /// <summary>Line-by-line YAML parser that drives <see cref="Yaml"/> tree construction.</summary>
    private sealed class YamlParser(string[] lines)
    {
        private int _pos;

        public Yaml ParseDocument()
        {
            // Skip YAML directives and document-start markers
            while (_pos < lines.Length)
            {
                var trimmed = lines[_pos].TrimStart();
                if (trimmed.StartsWith('%') || trimmed.StartsWith("---", StringComparison.Ordinal))
                {
                    _pos++;
                    continue;
                }

                break;
            }

            return ParseNode(-1) ?? Yaml.Null;
        }

        private Yaml? ParseNode(int parentIndent)
        {
            while (_pos < lines.Length)
            {
                var raw = lines[_pos];
                var stripped = StripComment(raw).TrimEnd();

                if (string.IsNullOrWhiteSpace(stripped))
                {
                    _pos++;
                    continue;
                }

                var indent = GetIndent(raw);
                if (indent <= parentIndent)
                {
                    return null;
                }

                var content = stripped.TrimStart();
                if (content.StartsWith("...", StringComparison.Ordinal))
                {
                    return null;
                }

                if (content == "-" || content.StartsWith("- ", StringComparison.Ordinal))
                {
                    return ParseBlockSequence(indent);
                }

                if (FindMappingColon(content) >= 0)
                {
                    return ParseBlockMapping(indent);
                }

                _pos++;
                return ParseScalarOrFlow(content);
            }

            return null;
        }

        private Yaml ParseBlockMapping(int baseIndent)
        {
            var dict = new Dictionary<string, Yaml>(StringComparer.Ordinal);

            while (_pos < lines.Length)
            {
                var raw = lines[_pos];
                var stripped = StripComment(raw).TrimEnd();

                if (string.IsNullOrWhiteSpace(stripped))
                {
                    _pos++;
                    continue;
                }

                var indent = GetIndent(raw);
                if (indent != baseIndent)
                {
                    break;
                }

                var content = stripped.TrimStart();
                if (content.StartsWith("...", StringComparison.Ordinal) ||
                    content.StartsWith("---", StringComparison.Ordinal))
                {
                    break;
                }

                var colonIdx = FindMappingColon(content);
                if (colonIdx < 0)
                {
                    break;
                }

                _pos++;

                var key = UnquoteScalar(content[..colonIdx].Trim());
                var valueRaw = StripComment(content[(colonIdx + 1)..].TrimStart()).TrimEnd();

                Yaml value;
                if (string.IsNullOrEmpty(valueRaw))
                {
                    value = ParseNode(baseIndent) ?? Yaml.Null;
                }
                else if (IsBlockScalarIndicator(valueRaw, '|'))
                {
                    value = ParseLiteralBlockScalar(baseIndent, valueRaw);
                }
                else if (IsBlockScalarIndicator(valueRaw, '>'))
                {
                    value = ParseFoldedBlockScalar(baseIndent, valueRaw);
                }
                else
                {
                    value = ParseScalarOrFlow(valueRaw);
                }

                dict[key] = value;
            }

            return Yaml.FromDictionary(dict);
        }

        private Yaml ParseBlockSequence(int baseIndent)
        {
            var list = new List<Yaml>();

            while (_pos < lines.Length)
            {
                var raw = lines[_pos];
                var stripped = StripComment(raw).TrimEnd();

                if (string.IsNullOrWhiteSpace(stripped))
                {
                    _pos++;
                    continue;
                }

                var indent = GetIndent(raw);
                if (indent != baseIndent)
                {
                    break;
                }

                var content = stripped.TrimStart();
                if (content.StartsWith("...", StringComparison.Ordinal) ||
                    content.StartsWith("---", StringComparison.Ordinal))
                {
                    break;
                }

                if (content != "-" && !content.StartsWith("- ", StringComparison.Ordinal))
                {
                    break;
                }

                _pos++;

                // Everything after the "- " prefix
                var after = content.Length > 2 ? content[2..] : string.Empty;

                Yaml item;
                if (string.IsNullOrWhiteSpace(after))
                {
                    item = ParseNode(baseIndent) ?? Yaml.Null;
                }
                else
                {
                    var colonIdx = FindMappingColon(after.TrimStart());
                    if (colonIdx >= 0)
                    {
                        // Inline mapping item: gather first key:value + continuation at deeper indent
                        item = ParseInlineSequenceItem(after.TrimStart(), baseIndent + 2);
                    }
                    else
                    {
                        item = ParseScalarOrFlow(after.TrimStart());
                    }
                }

                list.Add(item);
            }

            return Yaml.FromList(list);
        }

        /// <summary>
        /// Parses a sequence item whose first key:value pair is on the same line as the <c>- </c> marker,
        /// then merges additional key:value pairs at <paramref name="continuationIndent"/> into the same mapping.
        /// </summary>
        private Yaml ParseInlineSequenceItem(string firstLine, int continuationIndent)
        {
            var dict = new Dictionary<string, Yaml>(StringComparer.Ordinal);

            var colonIdx = FindMappingColon(firstLine);
            if (colonIdx >= 0)
            {
                var key = UnquoteScalar(firstLine[..colonIdx].Trim());
                var valueRaw = StripComment(firstLine[(colonIdx + 1)..].TrimStart()).TrimEnd();

                dict[key] = string.IsNullOrEmpty(valueRaw)
                    ? ParseNode(continuationIndent - 1) ?? Yaml.Null
                    : ParseScalarOrFlow(valueRaw);
            }

            while (_pos < lines.Length)
            {
                var raw = lines[_pos];
                var stripped = StripComment(raw).TrimEnd();

                if (string.IsNullOrWhiteSpace(stripped))
                {
                    _pos++;
                    continue;
                }

                var indent = GetIndent(raw);
                if (indent < continuationIndent)
                {
                    break;
                }

                var content = stripped.TrimStart();
                if (content.StartsWith("...", StringComparison.Ordinal) ||
                    content.StartsWith("---", StringComparison.Ordinal) ||
                    content == "-" ||
                    content.StartsWith("- ", StringComparison.Ordinal))
                {
                    break;
                }

                var cIdx = FindMappingColon(content);
                if (cIdx < 0)
                {
                    break;
                }

                _pos++;

                var k = UnquoteScalar(content[..cIdx].Trim());
                var v = StripComment(content[(cIdx + 1)..].TrimStart()).TrimEnd();

                dict[k] = string.IsNullOrEmpty(v)
                    ? ParseNode(indent) ?? Yaml.Null
                    : ParseScalarOrFlow(v);
            }

            return dict.Count > 0 ? Yaml.FromDictionary(dict) : Yaml.Null;
        }

        private Yaml ParseLiteralBlockScalar(int mappingIndent, string indicator)
        {
            var chomp = GetChomp(indicator);
            var currentIndent = -1;
            var collected = new List<string>();

            while (_pos < lines.Length)
            {
                var raw = lines[_pos];

                if (string.IsNullOrWhiteSpace(raw))
                {
                    collected.Add(string.Empty);
                    _pos++;
                    continue;
                }

                var indent = GetIndent(raw);
                if (currentIndent < 0)
                {
                    if (indent <= mappingIndent)
                    {
                        break;
                    }

                    currentIndent = indent;
                }

                if (indent < currentIndent)
                {
                    break;
                }

                collected.Add(raw[currentIndent..].TrimEnd('\r', '\n'));
                _pos++;
            }

            return Yaml.FromString(ApplyChomp(collected, chomp, literal: true));
        }

        private Yaml ParseFoldedBlockScalar(int mappingIndent, string indicator)
        {
            var chomp = GetChomp(indicator);
            var currentIndent = -1;
            var collected = new List<string>();

            while (_pos < lines.Length)
            {
                var raw = lines[_pos];

                if (string.IsNullOrWhiteSpace(raw))
                {
                    collected.Add(string.Empty);
                    _pos++;
                    continue;
                }

                var indent = GetIndent(raw);
                if (currentIndent < 0)
                {
                    if (indent <= mappingIndent)
                    {
                        break;
                    }

                    currentIndent = indent;
                }

                if (indent < currentIndent)
                {
                    break;
                }

                collected.Add(raw[currentIndent..].TrimEnd('\r', '\n'));
                _pos++;
            }

            return Yaml.FromString(ApplyChomp(collected, chomp, literal: false));
        }

        private static Yaml ParseScalarOrFlow(string raw)
        {
            var trimmed = raw.Trim();

            if (trimmed.StartsWith('{'))
            {
                return ParseFlowMapping(trimmed);
            }

            if (trimmed.StartsWith('['))
            {
                return ParseFlowSequence(trimmed);
            }

            return ParseScalar(trimmed);
        }

        private static Yaml ParseFlowMapping(string raw)
        {
            var inner = raw.Length >= 2 ? raw[1..^1].Trim() : string.Empty;
            var dict = new Dictionary<string, Yaml>(StringComparer.Ordinal);

            if (string.IsNullOrEmpty(inner))
            {
                return Yaml.FromDictionary(dict);
            }

            foreach (var segment in SplitFlowItems(inner))
            {
                var pair = segment.Trim();
                var colonIdx = FindMappingColon(pair);
                if (colonIdx < 0)
                {
                    continue;
                }

                var key = UnquoteScalar(pair[..colonIdx].Trim());
                dict[key] = ParseScalar(pair[(colonIdx + 1)..].Trim());
            }

            return Yaml.FromDictionary(dict);
        }

        private static Yaml ParseFlowSequence(string raw)
        {
            var inner = raw.Length >= 2 ? raw[1..^1].Trim() : string.Empty;
            var list = new List<Yaml>();

            if (string.IsNullOrEmpty(inner))
            {
                return Yaml.FromList(list);
            }

            foreach (var segment in SplitFlowItems(inner))
            {
                list.Add(ParseScalar(segment.Trim()));
            }

            return Yaml.FromList(list);
        }

        private static Yaml ParseScalar(string raw)
        {
            if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            {
                return Yaml.FromString(UnescapeDoubleQuoted(raw[1..^1]));
            }

            if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
            {
                return Yaml.FromString(raw[1..^1].Replace("''", "'"));
            }

            return raw switch
            {
                "" or "~" or "null" or "Null" or "NULL" => Yaml.Null,
                "true" or "True" or "TRUE" or "yes" or "Yes" or "YES" or "on" or "On" or "ON" => Yaml.True,
                "false" or "False" or "FALSE" or "no" or "No" or "NO" or "off" or "Off" or "OFF" => Yaml.False,
                _ => ParseNumericOrString(raw)
            };
        }

        private static Yaml ParseNumericOrString(string raw)
        {
            if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
            {
                return Yaml.FromNumber(number);
            }

            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(raw.AsSpan(2), NumberStyles.HexNumber, null, out var hex))
            {
                return Yaml.FromNumber(hex);
            }

            if (raw.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return Yaml.FromNumber(Convert.ToInt64(raw[2..], 8));
                }
                catch (FormatException) { }
            }

            return Yaml.FromString(raw);
        }

        /// <summary>Strips a trailing <c>#</c> comment, respecting quoted strings.</summary>
        private static string StripComment(string line)
        {
            bool inSingle = false, inDouble = false;

            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (c == '\'' && !inDouble) { inSingle = !inSingle; continue; }
                if (c == '"' && !inSingle) { inDouble = !inDouble; continue; }

                // A comment marker must be preceded by whitespace to disambiguate from values like URLs
                if (c == '#' && !inSingle && !inDouble && i > 0 && char.IsWhiteSpace(line[i - 1]))
                {
                    return line[..i];
                }
            }

            return line;
        }

        /// <summary>
        /// Returns the index of the first colon that acts as a mapping-key delimiter:
        /// outside of quotes and followed by whitespace or end-of-string.
        /// Returns -1 when no such delimiter is present.
        /// </summary>
        private static int FindMappingColon(string s)
        {
            bool inSingle = false, inDouble = false;

            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];

                if (c == '\'' && !inDouble) { inSingle = !inSingle; continue; }
                if (c == '"' && !inSingle) { inDouble = !inDouble; continue; }

                if (c == ':' && !inSingle && !inDouble)
                {
                    if (i + 1 >= s.Length || s[i + 1] == ' ' || s[i + 1] == '\t')
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        /// <summary>Splits comma-delimited flow-collection items, respecting nested brackets and quotes.</summary>
        private static List<string> SplitFlowItems(string inner)
        {
            var results = new List<string>();
            bool inSingle = false, inDouble = false;
            var depth = 0;
            var start = 0;

            for (int i = 0; i < inner.Length; i++)
            {
                var c = inner[i];

                if (c == '\'' && !inDouble) { inSingle = !inSingle; continue; }
                if (c == '"' && !inSingle) { inDouble = !inDouble; continue; }

                if (!inSingle && !inDouble)
                {
                    if (c is '[' or '{') { depth++; continue; }
                    if (c is ']' or '}') { depth--; continue; }

                    if (c == ',' && depth == 0)
                    {
                        results.Add(inner[start..i]);
                        start = i + 1;
                    }
                }
            }

            results.Add(inner[start..]);
            return results;
        }

        private static string UnquoteScalar(string raw)
        {
            if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            {
                return UnescapeDoubleQuoted(raw[1..^1]);
            }

            if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
            {
                return raw[1..^1].Replace("''", "'");
            }

            return raw;
        }

        private static string UnescapeDoubleQuoted(string inner)
        {
            if (!inner.Contains('\\'))
            {
                return inner;
            }

            var sb = new StringBuilder(inner.Length);
            var i = 0;

            while (i < inner.Length)
            {
                if (inner[i] != '\\')
                {
                    sb.Append(inner[i++]);
                    continue;
                }

                i++;
                if (i >= inner.Length)
                {
                    break;
                }

                sb.Append(inner[i] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\\' => '\\',
                    '"' => '"',
                    '0' => '\0',
                    'a' => '\a',
                    'b' => '\b',
                    'f' => '\f',
                    'v' => '\v',
                    _ => inner[i]
                });

                i++;
            }

            return sb.ToString();
        }

        private static int GetIndent(string line)
        {
            var i = 0;
            while (i < line.Length && line[i] == ' ')
            {
                i++;
            }

            return i;
        }

        private static char GetChomp(string indicator)
        {
            // YAML block chomping: + keeps all trailing newlines, - strips all, default clips to one
            if (indicator.Contains('+')) return '+';
            if (indicator.Contains('-')) return '-';
            return '=';
        }

        private static bool IsBlockScalarIndicator(string s, char marker)
        {
            if (s.Length == 0 || s[0] != marker) return false;
            if (s.Length == 1) return true;
            return s[1] is '+' or '-' or '1' or '2' or '3' or '4' or '5' or '6' or '7' or '8' or '9';
        }

        /// <summary>
        /// Joins collected block-scalar lines and applies YAML chomp semantics:
        /// clip (<c>=</c>) → exactly one trailing newline,
        /// strip (<c>-</c>) → no trailing newlines,
        /// keep (<c>+</c>) → all trailing newlines preserved.
        /// For folded scalars, adjacent non-empty lines are joined with a space.
        /// </summary>
        private static string ApplyChomp(List<string> collected, char chomp, bool literal)
        {
            if (collected.Count == 0)
            {
                return string.Empty;
            }

            string joined;
            if (literal)
            {
                joined = string.Join("\n", collected);
            }
            else
            {
                var sb = new StringBuilder();
                for (int i = 0; i < collected.Count; i++)
                {
                    if (collected[i].Length == 0)
                    {
                        sb.Append('\n');
                    }
                    else if (i > 0 && collected[i - 1].Length > 0 && sb.Length > 0 && sb[^1] != '\n')
                    {
                        sb.Append(' ');
                        sb.Append(collected[i]);
                    }
                    else
                    {
                        sb.Append(collected[i]);
                    }
                }

                joined = sb.ToString();
            }

            return chomp switch
            {
                '-' => joined.TrimEnd('\n'),
                '+' => joined.EndsWith('\n') ? joined : joined + '\n',
                _ => joined.TrimEnd('\n') + '\n'
            };
        }
    }
}


