using System.CommandLine;

namespace apps.Tests;

/// <summary>
/// Covers command-line parsing set up by <see cref="UpdateCommand.Configure"/>.
/// Parsing never runs the command action, so the orchestrator and service provider are not needed.
/// </summary>
/// <remarks>
/// <see cref="UpdateCommand.Configure"/> changes static options, so the command is built once and shared.
/// </remarks>
public sealed class UpdateCommandTests
{
    private static readonly RootCommand RootCmd = CreateRootCommand();

    // Breaks if a --kind validator reads the value of a token that already failed AcceptOnlyFromAmong:
    // System.CommandLine then throws InvalidOperationException from Parse.
    [Test]
    public async Task Parse_UnknownKind_ReportsParseErrorInsteadOfThrowing()
    {
        var result = RootCmd.Parse(["--kind", "jetbrains-plugin"]);

        await Assert.That(result.Errors.Count).IsEqualTo(1);
        await Assert.That(result.Errors[0].Message).Contains("jetbrains-plugin");
    }

    // Breaks if --kind rejects a kind the results table shows ("dev") or accepts one no app has ("lib").
    [Test]
    [Arguments("app", true)]
    [Arguments("package", true)]
    [Arguments("dev", true)]
    [Arguments("service", true)]
    [Arguments("ext", true)]
    [Arguments("lib", false)]
    [Arguments("dep", false)]
    public async Task Parse_Kind_AcceptsOnlyRealKinds(string kind, bool accepted)
    {
        var result = RootCmd.Parse(["--kind", kind]);

        await Assert.That(result.Errors.Count == 0).IsEqualTo(accepted);
    }

    // Breaks if TryParseCliString reports success for a value that names no kind.
    [Test]
    public async Task TryParseCliString_UnknownValue_ReturnsFalse()
    {
        var parsed = AppKindExtensions.TryParseCliString("lib", out var kind);

        await Assert.That(parsed).IsFalse();
        await Assert.That(kind).IsNull();
    }

    private static RootCommand CreateRootCommand()
    {
        var rootCmd = new RootCommand();
        UpdateCommand.Configure(rootCmd, null!, null!);
        return rootCmd;
    }
}
