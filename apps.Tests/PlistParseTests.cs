using System.Xml;

namespace apps.Tests;

/// <summary>
/// Covers the XML-plist tree parser (<see cref="PlistReader.Plist"/>) that reads
/// macOS <c>Info.plist</c> dictionaries. The file/binary-conversion paths are not unit-tested
/// (they touch the filesystem and <c>plutil</c>); only the pure XML → tree mapping is.
/// </summary>
public sealed class PlistParseTests
{
    private const string SampleXml = """
                                     <?xml version="1.0" encoding="UTF-8"?>
                                     <plist version="1.0">
                                     <dict>
                                         <key>CFBundleName</key>
                                         <string>Widget</string>
                                         <key>CFBundleShortVersionString</key>
                                         <string>2.5.1</string>
                                         <key>CFBundleVersion</key>
                                         <integer>281596</integer>
                                         <key>LSMinimumSystemVersion</key>
                                         <real>13.0</real>
                                         <key>LSUIElement</key>
                                         <true/>
                                         <key>NSSupportsAutomaticTermination</key>
                                         <false/>
                                         <key>CFBundleURLTypes</key>
                                         <array>
                                             <string>http</string>
                                             <string>https</string>
                                         </array>
                                         <key>NSExtension</key>
                                         <dict>
                                             <key>NSExtensionPointIdentifier</key>
                                             <string>com.apple.Safari.web-extension</string>
                                         </dict>
                                     </dict>
                                     </plist>
                                     """;

    private static PlistReader.Plist Parse(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var root = doc.SelectSingleNode("/plist")!;
        return PlistReader.Plist.Parse(root) ?? throw new InvalidOperationException("parse returned null");
    }

    [Test]
    public async Task Parse_ReadsStringValues()
    {
        var plist = Parse(SampleXml);
        await Assert.That(plist.GetString("CFBundleName")).IsEqualTo("Widget");
        await Assert.That(plist.GetString("CFBundleShortVersionString")).IsEqualTo("2.5.1");
    }

    [Test]
    public async Task Parse_ReadsNumericValues()
    {
        var plist = Parse(SampleXml);
        await Assert.That(plist.GetNumber("CFBundleVersion")).IsEqualTo(281596m);
        await Assert.That(plist.GetNumber("LSMinimumSystemVersion")).IsEqualTo(13.0m);
    }

    [Test]
    public async Task Parse_ReadsBooleanValues()
    {
        var plist = Parse(SampleXml);
        await Assert.That(plist.GetBoolean("LSUIElement")).IsTrue();
        await Assert.That(plist.GetBoolean("NSSupportsAutomaticTermination")).IsFalse();
    }

    [Test]
    public async Task Parse_ReadsArrays()
    {
        var plist = Parse(SampleXml);
        var urls = plist.GetArray("CFBundleURLTypes");
        await Assert.That(urls).IsNotNull();
        await Assert.That(urls!.Count).IsEqualTo(2);
        await Assert.That(urls[0].GetString()).IsEqualTo("http");
        await Assert.That(urls[1].GetString()).IsEqualTo("https");
    }

    [Test]
    public async Task Parse_ReadsNestedDictionary()
    {
        var plist = Parse(SampleXml);
        await Assert.That(plist.TryGetValue("NSExtension", out var ext)).IsTrue();
        await Assert.That(ext!.GetString("NSExtensionPointIdentifier"))
            .IsEqualTo("com.apple.Safari.web-extension");
    }

    [Test]
    public async Task ContainsKey_ReflectsPresence()
    {
        var plist = Parse(SampleXml);
        await Assert.That(plist.ContainsKey("CFBundleName")).IsTrue();
        await Assert.That(plist.ContainsKey("DoesNotExist")).IsFalse();
    }

    [Test]
    public async Task GetString_MissingKey_ReturnsNull()
    {
        var plist = Parse(SampleXml);
        await Assert.That(plist.GetString("NoSuchKey")).IsNull();
    }

    [Test]
    public async Task Parse_EmptyDict_HasZeroCount()
    {
        const string xml = """
                           <?xml version="1.0" encoding="UTF-8"?>
                           <plist version="1.0">
                           <dict>
                           </dict>
                           </plist>
                           """;
        var plist = Parse(xml);
        await Assert.That(plist.Count).IsEqualTo(0);
    }
}
