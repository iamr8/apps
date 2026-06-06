using System.Text;
using System.Text.Json;

using apps.Components.Audit;
using apps.Tests.Fakes;

using Microsoft.Extensions.Logging.Abstractions;

namespace apps.Tests.Audit;

/// <summary>
/// Covers <see cref="OsvAuditChecker"/>: the package-name and ecosystem mapping seams, the
/// OSV response parser, the vulnerability projection, and the end-to-end audit flow against a
/// stubbed OSV API. Version-range matching itself lives in <see cref="OsvVulnerabilityChecker"/>
/// and is covered separately.
/// </summary>
public sealed class OsvAuditCheckerTests
{
    private static readonly FakeScanner Scanner = new() { Name = "Go", Kind = AppKind.DevTool };

    [Test]
    public async Task GetPackageName_LibraryWithUpdateInfo_UsesUpdateInfo()
    {
        var record = GoRecord("display-name", installed: "1.0.0", updateInfo: "github.com/owner/repo");

        await Assert.That(OsvAuditChecker.GetPackageName(record)).IsEqualTo("github.com/owner/repo");
    }

    [Test]
    public async Task GetPackageName_NonLibrary_UsesName()
    {
        var app = new DiscoveredApp(Scanner, "ripgrep", new AppIdentifier("Go", "Go"), AppKind.DevTool)
        {
            InstalledVersion = "1.0.0",
            Attribute = AppAttribute.DevTool,
            UpdateInfo = "github.com/owner/repo",
        };

        await Assert.That(OsvAuditChecker.GetPackageName(new AppRecord(app))).IsEqualTo("ripgrep");
    }

    [Test]
    public async Task GetPackageName_LibraryWithoutUpdateInfo_FallsBackToName()
    {
        var app = new DiscoveredApp(Scanner, "leftpad", new AppIdentifier("Go", "Go"), AppKind.DevTool)
        {
            InstalledVersion = "1.0.0",
            Attribute = AppAttribute.DevTool | AppAttribute.Library,
        };

        await Assert.That(OsvAuditChecker.GetPackageName(new AppRecord(app))).IsEqualTo("leftpad");
    }

    [Test]
    [Arguments("Go", "Go")]
    [Arguments("GoTools", "Go")]
    [Arguments("GoMod", "Go")]
    [Arguments("SwiftPM", "SwiftURL")]
    [Arguments("npm", "")]
    [Arguments("NuGet", "")]
    public async Task MapEcosystem_MapsScannerIdentifierToOsvEcosystem(string identifierName, string expected)
    {
        var app = new DiscoveredApp(Scanner, "pkg", new AppIdentifier(identifierName, identifierName), AppKind.DevTool)
        {
            InstalledVersion = "1.0.0",
            Attribute = AppAttribute.DevTool,
        };

        await Assert.That(OsvAuditChecker.MapEcosystem(new AppRecord(app))).IsEqualTo(expected);
    }

    [Test]
    public async Task ProjectVulnerabilities_CollapsesEntriesSharingAlias()
    {
        var matches = new[]
        {
            Match("GHSA-a", aliases: ["CVE-2024-1"], summary: "first", severity: VulnerabilitySeverity.High),
            Match("GHSA-b", aliases: ["CVE-2024-1"], summary: "duplicate", severity: VulnerabilitySeverity.Low),
            Match("GHSA-c", aliases: [], summary: "second", severity: VulnerabilitySeverity.Critical),
        };

        var projected = OsvAuditChecker.ProjectVulnerabilities(matches);

        await Assert.That(projected.Length).IsEqualTo(2);
        await Assert.That(projected[0].Id).IsEqualTo("CVE-2024-1");
        await Assert.That(projected[0].Summary).IsEqualTo("first");
        await Assert.That(projected[0].Severity).IsEqualTo(VulnerabilitySeverity.High);
        await Assert.That(projected[1].Id).IsEqualTo("GHSA-c");
        await Assert.That(projected[1].Summary).IsEqualTo("second");
    }

    [Test]
    public async Task ProjectVulnerabilities_NoAlias_KeysById()
    {
        var matches = new[] { Match("GHSA-only", aliases: [], summary: null, severity: VulnerabilitySeverity.Medium) };

        var projected = OsvAuditChecker.ProjectVulnerabilities(matches);

        await Assert.That(projected.Length).IsEqualTo(1);
        await Assert.That(projected[0].Id).IsEqualTo("GHSA-only");
    }

    [Test]
    public async Task ProjectVulnerabilities_NoMatches_ReturnsEmpty()
    {
        await Assert.That(OsvAuditChecker.ProjectVulnerabilities([])).IsEmpty();
    }

    [Test]
    public async Task ProjectVulnerabilities_NullSummary_FallsBackToDetails()
    {
        var match = new VulnerabilityMatch
        {
            Id = "GHSA-d",
            Summary = null,
            Details = "detailed description",
            Severity = VulnerabilitySeverity.Low,
        };

        var projected = OsvAuditChecker.ProjectVulnerabilities([match]);

        await Assert.That(projected[0].Summary).IsEqualTo("detailed description");
    }

    [Test]
    public async Task ParseOsvResponse_ReadsVulnerabilities()
    {
        const string json = """
                            {
                              "vulns": [
                                {
                                  "id": "GHSA-x",
                                  "summary": "bad bug",
                                  "aliases": ["CVE-2024-9999"],
                                  "affected": [
                                    {
                                      "package": { "name": "leftpad", "ecosystem": "Go" },
                                      "ranges": [
                                        { "type": "SEMVER", "events": [{ "introduced": "0" }, { "fixed": "2.0.0" }] }
                                      ]
                                    }
                                  ]
                                }
                              ]
                            }
                            """;
        var result = ParseOsvResponse(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Vulnerabilities).IsNotNull();
        await Assert.That(result.Vulnerabilities!.Count).IsEqualTo(1);
        await Assert.That(result.Vulnerabilities[0].Id).IsEqualTo("GHSA-x");
        await Assert.That(result.Vulnerabilities[0].Aliases![0]).IsEqualTo("CVE-2024-9999");
    }

    [Test]
    public async Task ParseOsvResponse_NoVulns_YieldsNullList()
    {
        var result = ParseOsvResponse("{}");
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Vulnerabilities).IsNull();
    }

    [Test]
    public async Task ParseOsvResponse_Malformed_Throws()
    {
        await Assert.That(() => ParseOsvResponse("not json")).Throws<JsonException>();
    }

    [Test]
    public async Task AuditAsync_PackageWithKnownCve_FlagsVulnerability()
    {
        var handler = new StubHttpMessageHandler().WithJson("/v1/query", VulnResponse);
        var checker = CreateChecker(handler);
        var record = GoRecord("github.com/owner/repo", installed: "1.0.0", updateInfo: "github.com/owner/repo");

        await checker.AuditAsync([record]);

        await Assert.That(handler.Requests.Count).IsEqualTo(1);
        await Assert.That(handler.Requests[0].AbsolutePath).IsEqualTo("/v1/query");
        await Assert.That(record.Vulnerabilities).IsNotNull();
        await Assert.That(record.Vulnerabilities!.Count).IsEqualTo(1);
        await Assert.That(record.Vulnerabilities[0].Id).IsEqualTo("CVE-2024-9999");
        await Assert.That(record.Vulnerabilities[0].Severity).IsEqualTo(VulnerabilitySeverity.Critical);
    }

    [Test]
    public async Task AuditAsync_PackageWithNoVulns_LeavesRecordClean()
    {
        var handler = new StubHttpMessageHandler().WithJson("/v1/query", """{ "vulns": [] }""");
        var checker = CreateChecker(handler);
        var record = GoRecord("github.com/owner/repo", installed: "1.0.0", updateInfo: "github.com/owner/repo");

        await checker.AuditAsync([record]);

        await Assert.That(handler.Requests.Count).IsEqualTo(1);
        await Assert.That(record.Vulnerabilities).IsNotNull();
        await Assert.That(record.Vulnerabilities!).IsEmpty();
    }

    [Test]
    public async Task AuditAsync_VersionAboveFix_NotFlagged()
    {
        var handler = new StubHttpMessageHandler().WithJson("/v1/query", VulnResponse);
        var checker = CreateChecker(handler);
        var record = GoRecord("github.com/owner/repo", installed: "2.5.0", updateInfo: "github.com/owner/repo");

        await checker.AuditAsync([record]);

        await Assert.That(record.Vulnerabilities).IsNotNull();
        await Assert.That(record.Vulnerabilities!).IsEmpty();
    }

    [Test]
    public async Task AuditAsync_HttpError_HandledGracefully()
    {
        var handler = new StubHttpMessageHandler().WithStatus("/v1/query", System.Net.HttpStatusCode.InternalServerError);
        var checker = CreateChecker(handler);
        var record = GoRecord("github.com/owner/repo", installed: "1.0.0", updateInfo: "github.com/owner/repo");

        await checker.AuditAsync([record]);

        await Assert.That(record.Vulnerabilities).IsNull();
    }

    [Test]
    public async Task AuditAsync_NoAuditableApps_MakesNoHttpCall()
    {
        var handler = new StubHttpMessageHandler().WithJson("/v1/query", VulnResponse);
        var checker = CreateChecker(handler);
        var app = new DiscoveredApp(Scanner, "pkg", new AppIdentifier("Go", "Go"), AppKind.DevTool)
        {
            InstalledVersion = "1.0.0",
            Attribute = AppAttribute.DevTool,
            OsvEcosystem = null,
        };

        await checker.AuditAsync([new AppRecord(app)]);

        await Assert.That(handler.Requests).IsEmpty();
    }

    private const string VulnResponse = """
                                        {
                                          "vulns": [
                                            {
                                              "id": "GHSA-x",
                                              "summary": "remote code execution",
                                              "upstream": ["CVE-2024-9999"],
                                              "severity": [
                                                { "type": "CVSS_V3", "score": "CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H" }
                                              ],
                                              "affected": [
                                                {
                                                  "package": { "name": "github.com/owner/repo", "ecosystem": "Go" },
                                                  "ranges": [
                                                    { "type": "SEMVER", "events": [{ "introduced": "0" }, { "fixed": "2.0.0" }] }
                                                  ]
                                                }
                                              ]
                                            }
                                          ]
                                        }
                                        """;

    private static OsvAuditChecker CreateChecker(StubHttpMessageHandler handler) =>
        new(new StubHttpClientFactory(handler), new LiveProgressRenderer([]), NullLogger<OsvAuditChecker>.Instance);

    private static AppRecord GoRecord(string name, string installed, string updateInfo)
    {
        var app = new DiscoveredApp(Scanner, name, new AppIdentifier("Go", "Go"), AppKind.DevTool)
        {
            InstalledVersion = installed,
            Attribute = AppAttribute.DevTool | AppAttribute.Library,
            UpdateInfo = updateInfo,
            OsvEcosystem = OsvEcosystemName.None,
        };
        return new AppRecord(app);
    }

    private static OsvResultEntry? ParseOsvResponse(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return JsonSerializer.Deserialize(bytes, OsvJsonContext.Default.OsvResultEntry);
    }

    private static VulnerabilityMatch Match(
        string id,
        IReadOnlyList<string> aliases,
        string? summary,
        VulnerabilitySeverity severity) =>
        new()
        {
            Id = id,
            Aliases = aliases,
            Summary = summary,
            Severity = severity,
        };
}
