namespace apps.Tests;

/// <summary>
/// Covers the AOT-safe <see cref="YamlReader"/> subset parser: mappings, sequences,
/// flow collections, scalar typing, block scalars, and comment handling.
/// </summary>
public sealed class YamlReaderTests
{
    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Parse_EmptyContent_ReturnsNull(string content)
    {
        await Assert.That(YamlReader.Parse(content)).IsNull();
    }

    [Test]
    public async Task Parse_SimpleMapping()
    {
        var yaml = YamlReader.Parse("name: apps\nversion: 1.2.3");

        await Assert.That(yaml!.IsMapping).IsTrue();
        await Assert.That(yaml.GetString("name")).IsEqualTo("apps");
        await Assert.That(yaml.GetString("version")).IsEqualTo("1.2.3");
    }

    [Test]
    public async Task Parse_NestedMapping()
    {
        const string content = """
                               metadata:
                                 name: widget
                                 labels:
                                   tier: backend
                               """;
        var yaml = YamlReader.Parse(content);

        await Assert.That(yaml!.TryGetValue("metadata", out var meta)).IsTrue();
        await Assert.That(meta!.GetString("name")).IsEqualTo("widget");
        await Assert.That(meta.TryGetValue("labels", out var labels)).IsTrue();
        await Assert.That(labels!.GetString("tier")).IsEqualTo("backend");
    }

    [Test]
    public async Task Parse_BlockSequence()
    {
        const string content = """
                               deps:
                                 - alpha
                                 - beta
                                 - gamma
                               """;
        var yaml = YamlReader.Parse(content);

        await Assert.That(yaml!.TryGetValue("deps", out var deps)).IsTrue();
        await Assert.That(deps!.IsSequence).IsTrue();
        await Assert.That(deps.Count).IsEqualTo(3);
        await Assert.That(deps.GetItem(0)!.GetString()).IsEqualTo("alpha");
        await Assert.That(deps.GetItem(2)!.GetString()).IsEqualTo("gamma");
    }

    [Test]
    public async Task Parse_FlowSequenceAndMapping()
    {
        var seq = YamlReader.Parse("items: [1, 2, 3]");
        await Assert.That(seq!.TryGetValue("items", out var items)).IsTrue();
        await Assert.That(items!.Count).IsEqualTo(3);
        await Assert.That(items.GetItem(1)!.GetNumber()).IsEqualTo(2m);

        var map = YamlReader.Parse("point: {x: 10, y: 20}");
        await Assert.That(map!.TryGetValue("point", out var point)).IsTrue();
        await Assert.That(point!.GetNumber("x")).IsEqualTo(10m);
        await Assert.That(point.GetNumber("y")).IsEqualTo(20m);
    }

    [Test]
    public async Task Parse_ScalarTypes()
    {
        const string content = """
                               s: hello
                               n: 42
                               f: 3.14
                               b1: true
                               b2: no
                               nothing: null
                               """;
        var yaml = YamlReader.Parse(content)!;

        await Assert.That(yaml.GetString("s")).IsEqualTo("hello");
        await Assert.That(yaml.GetNumber("n")).IsEqualTo(42m);
        await Assert.That(yaml.GetNumber("f")).IsEqualTo(3.14m);
        await Assert.That(yaml.GetBoolean("b1")).IsTrue();
        await Assert.That(yaml.GetBoolean("b2")).IsFalse();
        await Assert.That(yaml.TryGetValue("nothing", out var nothing)).IsTrue();
        await Assert.That(nothing!.IsNull).IsTrue();
    }

    [Test]
    public async Task Parse_QuotedStringsAndEscapes()
    {
        const string content = """
                               single: 'it''s here'
                               double: "line\nbreak"
                               hashy: "value # not a comment"
                               """;
        var yaml = YamlReader.Parse(content)!;

        await Assert.That(yaml.GetString("single")).IsEqualTo("it's here");
        await Assert.That(yaml.GetString("double")).IsEqualTo("line\nbreak");
        await Assert.That(yaml.GetString("hashy")).IsEqualTo("value # not a comment");
    }

    [Test]
    public async Task Parse_StripsTrailingComments()
    {
        var yaml = YamlReader.Parse("name: apps   # the tool name")!;
        await Assert.That(yaml.GetString("name")).IsEqualTo("apps");
    }

    [Test]
    public async Task Parse_LiteralBlockScalar_PreservesNewlines()
    {
        const string content = """
                               script: |
                                 line one
                                 line two
                               """;
        var yaml = YamlReader.Parse(content)!;
        await Assert.That(yaml.GetString("script")).IsEqualTo("line one\nline two\n");
    }

    [Test]
    public async Task Parse_FoldedBlockScalar_JoinsWithSpaces()
    {
        const string content = """
                               text: >
                                 word one
                                 word two
                               """;
        var yaml = YamlReader.Parse(content)!;
        await Assert.That(yaml.GetString("text")).IsEqualTo("word one word two\n");
    }

    [Test]
    public async Task Parse_SkipsDocumentMarkersAndDirectives()
    {
        const string content = """
                               %YAML 1.2
                               ---
                               key: value
                               """;
        var yaml = YamlReader.Parse(content)!;
        await Assert.That(yaml.GetString("key")).IsEqualTo("value");
    }

    [Test]
    public async Task Parse_SequenceOfMappings()
    {
        const string content = """
                               packages:
                                 - name: alpha
                                   version: 1.0.0
                                 - name: beta
                                   version: 2.0.0
                               """;
        var yaml = YamlReader.Parse(content)!;
        await Assert.That(yaml.TryGetValue("packages", out var pkgs)).IsTrue();
        await Assert.That(pkgs!.Count).IsEqualTo(2);
        await Assert.That(pkgs.GetItem(0)!.GetString("name")).IsEqualTo("alpha");
        await Assert.That(pkgs.GetItem(1)!.GetString("version")).IsEqualTo("2.0.0");
    }
}
