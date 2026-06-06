using apps.Components.Windows;

namespace apps.Tests.Components;

/// <summary>
/// Covers the platform-independent parsing seams of <see cref="WindowsApplicationsScanner"/>: the
/// fixed-width <c>winget list</c> table parser, the single-row slicer, and the <c>winget show</c>
/// version extractor. The registry discovery is Windows-only and is therefore not exercised here.
/// </summary>
public sealed class WindowsApplicationsScannerTests
{
    private const string Header = "Name              Id                     Version      Available    Source";

    [Test]
    public async Task ParseWingetList_ReadsNameIdAndVersion()
    {
        const string output = """
                              Name              Id                     Version      Available    Source
                              -----------------------------------------------------------------------------
                              Git               Git.Git                2.43.0                    winget
                              7-Zip             7zip.7zip              23.01                     winget
                              """;
        var packages = WindowsApplicationsScanner.ParseWingetList(output);

        await Assert.That(packages.Count).IsEqualTo(2);
        await Assert.That(packages[0]).IsEqualTo(new WindowsApplicationsScanner.WingetPackage("Git", "Git.Git", "2.43.0"));
        await Assert.That(packages[1]).IsEqualTo(new WindowsApplicationsScanner.WingetPackage("7-Zip", "7zip.7zip", "23.01"));
    }

    [Test]
    public async Task ParseWingetList_NoHeader_ReturnsEmpty()
    {
        const string output = """
                              No installed package found matching input criteria.
                              """;
        var packages = WindowsApplicationsScanner.ParseWingetList(output);

        await Assert.That(packages).IsEmpty();
    }

    [Test]
    public async Task ParseWingetList_OnlyHeaderAndRule_ReturnsEmpty()
    {
        const string output = """
                              Name              Id                     Version      Available    Source
                              -----------------------------------------------------------------------------
                              """;
        var packages = WindowsApplicationsScanner.ParseWingetList(output);

        await Assert.That(packages).IsEmpty();
    }

    [Test]
    public async Task ParseWingetList_SkipsBlankAndIncompleteRows()
    {
        const string output = """
                              Name              Id                     Version      Available    Source
                              -----------------------------------------------------------------------------
                              Git               Git.Git                2.43.0                    winget

                              OrphanName
                              """;
        var packages = WindowsApplicationsScanner.ParseWingetList(output);

        await Assert.That(packages.Count).IsEqualTo(1);
        await Assert.That(packages[0].Name).IsEqualTo("Git");
    }

    [Test]
    public async Task ParseWingetList_DropsRowsWithTruncatedNameOrId()
    {
        const string output = """
                              Name              Id                     Version      Available    Source
                              -----------------------------------------------------------------------------
                              Microsoft Visu…   Microsoft.VisualStu…   17.8.0                    winget
                              Git               Git.Git                2.43.0                    winget
                              """;
        var packages = WindowsApplicationsScanner.ParseWingetList(output);

        await Assert.That(packages.Count).IsEqualTo(1);
        await Assert.That(packages[0].Name).IsEqualTo("Git");
    }

    [Test]
    public async Task ParseWingetList_DuplicateName_LastRowWins()
    {
        const string output = """
                              Name              Id                     Version      Available    Source
                              -----------------------------------------------------------------------------
                              Git               Git.Git                2.43.0                    winget
                              Git               Git.Git                2.44.0                    winget
                              """;
        var packages = WindowsApplicationsScanner.ParseWingetList(output);

        await Assert.That(packages.Count).IsEqualTo(2);
        await Assert.That(packages[^1].Version).IsEqualTo("2.44.0");
    }

    [Test]
    public async Task ParseWingetList_WindowsCarriageReturns_AreTrimmed()
    {
        var output = string.Join("\r\n",
            "Name              Id                     Version      Available    Source",
            "-----------------------------------------------------------------------------",
            "Git               Git.Git                2.43.0                    winget");
        var packages = WindowsApplicationsScanner.ParseWingetList(output);

        await Assert.That(packages.Count).IsEqualTo(1);
        await Assert.That(packages[0].Version).IsEqualTo("2.43.0");
    }

    [Test]
    public async Task ParseWingetList_UnicodePackageName_IsPreserved()
    {
        var output = string.Join('\n',
            Header,
            "-----------------------------------------------------------------------------",
            "日本語アプリ            Vendor.Niho            1.2.3                     winget");
        var packages = WindowsApplicationsScanner.ParseWingetList(output);

        await Assert.That(packages.Count).IsEqualTo(1);
        await Assert.That(packages[0].Name).IsEqualTo("日本語アプリ");
        await Assert.That(packages[0].Id).IsEqualTo("Vendor.Niho");
    }

    [Test]
    public async Task ParseWingetList_RowWithoutSourceColumn_StillParsesVersion()
    {
        const string output = """
                              Name              Id                     Version
                              -----------------------------------------------------
                              Git               Git.Git                2.43.0
                              """;
        var packages = WindowsApplicationsScanner.ParseWingetList(output);

        await Assert.That(packages.Count).IsEqualTo(1);
        await Assert.That(packages[0].Version).IsEqualTo("2.43.0");
    }

    [Test]
    public async Task TryParseWingetRow_SeparatorRule_ReturnsFalse()
    {
        var idCol = Header.IndexOf("Id", StringComparison.Ordinal);
        var versionCol = Header.IndexOf("Version", StringComparison.Ordinal);

        var parsed = WindowsApplicationsScanner.TryParseWingetRow(
            "-----------------------------------------------------------------------------",
            idCol, versionCol, int.MaxValue, out _);

        await Assert.That(parsed).IsFalse();
    }

    [Test]
    public async Task TryParseWingetRow_BlankLine_ReturnsFalse()
    {
        var parsed = WindowsApplicationsScanner.TryParseWingetRow("   ", 18, 39, int.MaxValue, out _);
        await Assert.That(parsed).IsFalse();
    }

    [Test]
    public async Task TryParseWingetRow_WellFormedRow_ParsesAllColumns()
    {
        const string row = "Git               Git.Git                2.43.0                    winget";
        var idCol = Header.IndexOf("Id", StringComparison.Ordinal);
        var versionCol = Header.IndexOf("Version", StringComparison.Ordinal);
        var sourceCol = Header.IndexOf("Source", StringComparison.Ordinal);

        var parsed = WindowsApplicationsScanner.TryParseWingetRow(row, idCol, versionCol, sourceCol, out var package);

        await Assert.That(parsed).IsTrue();
        await Assert.That(package.Name).IsEqualTo("Git");
        await Assert.That(package.Id).IsEqualTo("Git.Git");
        await Assert.That(package.Version).IsEqualTo("2.43.0");
    }

    [Test]
    public async Task TryParseWingetRow_MissingVersion_YieldsNullVersion()
    {
        const string row = "Git               Git.Git";
        var idCol = Header.IndexOf("Id", StringComparison.Ordinal);
        var versionCol = Header.IndexOf("Version", StringComparison.Ordinal);

        var parsed = WindowsApplicationsScanner.TryParseWingetRow(row, idCol, versionCol, int.MaxValue, out var package);

        await Assert.That(parsed).IsTrue();
        await Assert.That(package.Version).IsNull();
    }

    [Test]
    [Arguments("Version: 2.43.0", "2.43.0")]
    [Arguments("version: 1.0.0", "1.0.0")]
    [Arguments("Version:   3.2.1  ", "3.2.1")]
    public async Task ExtractWingetVersion_ReadsFirstVersionLine(string line, string expected)
    {
        var output = string.Join('\n', "Found Git [Git.Git]", line, "Publisher: Git");
        await Assert.That(WindowsApplicationsScanner.ExtractWingetVersion(output)).IsEqualTo(expected);
    }

    [Test]
    public async Task ExtractWingetVersion_TakesFirstWhenMultiplePresent()
    {
        var output = string.Join('\n', "Version: 2.43.0", "Version: 9.9.9");
        await Assert.That(WindowsApplicationsScanner.ExtractWingetVersion(output)).IsEqualTo("2.43.0");
    }

    [Test]
    [Arguments("Version: Unknown")]
    [Arguments("Version: unknown")]
    [Arguments("Version:")]
    [Arguments("Version:    ")]
    public async Task ExtractWingetVersion_NoConcreteVersion_ReturnsNull(string line)
    {
        await Assert.That(WindowsApplicationsScanner.ExtractWingetVersion(line)).IsNull();
    }

    [Test]
    public async Task ExtractWingetVersion_NoVersionLine_ReturnsNull()
    {
        var output = string.Join('\n', "Found Git [Git.Git]", "Publisher: Git");
        await Assert.That(WindowsApplicationsScanner.ExtractWingetVersion(output)).IsNull();
    }
}
