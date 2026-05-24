# Adding a New Component

To add support for a new ecosystem (e.g. Rust, Python/pip):

1. Create a folder under `apps/Components/YourComponent/`.

2. Implement a **scanner** that implements `IScanner`:

```csharp
public sealed class YourScanner(IProcessRunner processRunner) : IScanner
{
    public string Name => "YourComponent";

    public bool IsAvailable()
        => File.Exists("/path/to/tool");

    public async IAsyncEnumerable<DiscoveredApp> ScanAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Discover installed items and yield them
        yield return new DiscoveredApp(
            Name: "example-package",
            Scanner: Name,
            Kind: AppKind.Packages,
            InstalledVersion: "1.0.0",
            SuggestedMethod: UpdateMethod.PackageRegistry,
            SuggestedMethodDetail: "example-package");
    }
}
```

3. Implement a **checker** that implements `IUpdateChecker`:

```csharp
public sealed class YourChecker(IHttpClientFactory httpClientFactory) : IUpdateChecker
{
    public UpdateMethod Method => UpdateMethod.PackageRegistry;
    public string DisplayName => "YourComponent Registry";
    public (string Label, string? Qualifier)? SourceOverride => ("YourComponent", null);

    public bool CanCheck(AppRecord app)
        => app is { UpdateMethod: UpdateMethod.PackageRegistry, Scanner: "YourComponent" };

    public async Task<UpdateCheckResult> CheckAsync(AppRecord app, CancellationToken cancellationToken)
    {
        // Fetch the latest version from your registry
        // Compare with app.InstalledVersion
        // Return the result
    }

    public async Task<IReadOnlyList<UpdateCheckResult>> CheckBatchAsync(
        IReadOnlyList<AppRecord> apps, CancellationToken cancellationToken)
    {
        return await Task.WhenAll(apps.Select(a => CheckAsync(a, cancellationToken)));
    }

    public async IAsyncEnumerable<UpdateCheckResult> CheckStreamAsync(
        IReadOnlyList<AppRecord> apps,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var task in Task.WhenEach(apps.Select(a => CheckAsync(a, cancellationToken))))
        {
            yield return await task;
        }
    }
}
```

4. Create a **registration** extension method:

```csharp
public static class YourComponentRegistration
{
    public static IServiceCollection AddYourComponent(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, YourScanner>();
        services.AddSingleton<IUpdateChecker, YourChecker>();
        return services;
    }
}
```

5. Chain it in `Components/ComponentRegistration.cs`:

```csharp
services.AddYourComponent();
```

## Key Rules

- All I/O must be `async/await` with `CancellationToken` propagated everywhere.
- Use `IProcessRunner` for shell commands (never spawn processes directly).
- Use `IHttpClientFactory` named clients for HTTP (never create raw `HttpClient`).
- All JSON must use source-generated `JsonSerializerContext` (AOT-safe).
- Scanners return `IAsyncEnumerable<DiscoveredApp>`.
- The tool must remain AOT-compatible: no reflection, no `dynamic`, no runtime assembly loading.

