namespace apps.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IProcessRunner"/> for tests. Maps an <c>exe args</c> command line to a
/// canned <see cref="ProcessResult"/> and records every invocation for assertions.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Dictionary<string, ProcessResult> _responses = new(StringComparer.Ordinal);

    /// <summary>Every command line that was executed, in call order.</summary>
    public List<string> Invocations { get; } = [];

    /// <summary>Registers stdout (exit 0) for the exact <paramref name="exe"/> + <paramref name="args"/> pair.</summary>
    public FakeProcessRunner WithSuccess(string exe, string args, string stdout)
    {
        _responses[Key(exe, args)] = new ProcessResult(0, stdout, string.Empty);
        return this;
    }

    /// <summary>Registers a non-zero exit with the given stderr for the exact command.</summary>
    public FakeProcessRunner WithFailure(string exe, string args, string stderr, int exitCode = 1)
    {
        _responses[Key(exe, args)] = new ProcessResult(exitCode, string.Empty, stderr);
        return this;
    }

    public Task<ProcessResult> RunAsync(string exe, string args, CancellationToken cancellationToken = default)
    {
        Invocations.Add(Key(exe, args));

        return _responses.TryGetValue(Key(exe, args), out var result)
            ? Task.FromResult(result)
            : Task.FromResult(new ProcessResult(127, string.Empty, $"no fake response registered for: {Key(exe, args)}"));
    }

    public async Task<string> ReadOutputAsync(string exe, string args, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(exe, args, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException($"`{exe} {args}` exited {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }

    private static string Key(string exe, string args) => $"{exe} {args}";
}
