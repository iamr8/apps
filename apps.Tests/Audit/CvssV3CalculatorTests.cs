using apps.Components.Audit;

namespace apps.Tests.Audit;

/// <summary>
/// Verifies <see cref="CvssV3Calculator"/> against published CVSS v3.1 base scores.
/// Golden vectors are hand-verified against the first.org specification, Section 7.4.
/// </summary>
public sealed class CvssV3CalculatorTests
{
    [Test]
    [Arguments("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H", 9.8)] // Critical, scope unchanged
    [Arguments("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:C/C:H/I:H/A:H", 10.0)] // Scope changed, max
    [Arguments("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:N/I:N/A:N", 0.0)] // No impact
    [Arguments("CVSS:3.1/AV:L/AC:L/PR:L/UI:N/S:U/C:H/I:H/A:H", 7.8)] // Local privilege escalation
    [Arguments("CVSS:3.1/AV:N/AC:L/PR:N/UI:R/S:C/C:L/I:L/A:N", 6.1)] // Reflected XSS
    [Arguments("CVSS:3.1/AV:L/AC:L/PR:L/UI:N/S:U/C:H/I:N/A:N", 5.5)] // Local info disclosure
    public async Task GetSeverityScore_MatchesPublishedBaseScore(string vector, double expected)
    {
        var score = CvssV3Calculator.GetSeverityScore(vector);
        await Assert.That(score).IsEqualTo(expected).Within(0.01);
    }

    [Test]
    public async Task GetSeverityScore_AcceptsCvss30Prefix()
    {
        var score = CvssV3Calculator.GetSeverityScore("CVSS:3.0/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H");
        await Assert.That(score).IsEqualTo(9.8).Within(0.01);
    }

    [Test]
    public async Task GetSeverityScore_WorksWithoutVersionPrefix()
    {
        var score = CvssV3Calculator.GetSeverityScore("AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H");
        await Assert.That(score).IsEqualTo(9.8).Within(0.01);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task GetSeverityScore_EmptyVector_Throws(string vector)
    {
        await Assert.That(() => CvssV3Calculator.GetSeverityScore(vector)).Throws<ArgumentException>();
    }

    [Test]
    public async Task GetSeverityScore_MissingRequiredMetric_Throws()
    {
        // No Availability (A) metric.
        await Assert.That(() => CvssV3Calculator.GetSeverityScore("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H"))
            .Throws<FormatException>();
    }

    [Test]
    public async Task GetSeverityScore_MalformedSegment_Throws()
    {
        await Assert.That(() => CvssV3Calculator.GetSeverityScore("CVSS:3.1/AVN/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H"))
            .Throws<FormatException>();
    }

    [Test]
    public async Task GetSeverityScore_InvalidMetricValue_Throws()
    {
        // "AV:X" — X is not a valid Attack Vector value.
        await Assert.That(() => CvssV3Calculator.GetSeverityScore("CVSS:3.1/AV:X/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H"))
            .Throws<FormatException>();
    }

    [Test]
    public async Task GetSeverityScore_UnsupportedVersion_Throws()
    {
        await Assert.That(() => CvssV3Calculator.GetSeverityScore("CVSS:2.0/AV:N/AC:L/Au:N/C:P/I:P/A:P"))
            .Throws<NotSupportedException>();
    }
}
