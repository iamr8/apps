namespace apps.Infrastructure;

/// <summary>Result of a subprocess invocation.</summary>
public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError
)
{
    public bool Success => ExitCode == 0;
}