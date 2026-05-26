using System.Text.Json;
using System.Text.Json.Serialization;

namespace apps.Infrastructure;

/// <summary>
/// Manages pinned packages. Pinned packages are excluded from update-available reporting.
/// Pin data is stored in <c>~/.local/share/apps/pinned.json</c>.
/// </summary>
public sealed class PinManager
{
    private static readonly string PinFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "apps", "pinned.json");

    private Dictionary<string, PinEntry>? _pins;

    /// <summary>Loads the pin file from disk. Safe to call multiple times.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_pins is not null)
        {
            return;
        }

        if (!File.Exists(PinFilePath))
        {
            _pins = new Dictionary<string, PinEntry>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        await using var stream = File.OpenRead(PinFilePath);
        var data = await JsonSerializer.DeserializeAsync(stream, PinJsonContext.Default.PinFile, cancellationToken).ConfigureAwait(false);

        _pins = new Dictionary<string, PinEntry>(StringComparer.OrdinalIgnoreCase);
        if (data?.Pins is not null)
        {
            foreach (var pin in data.Pins)
            {
                _pins[pin.Name] = pin;
            }
        }
    }

    /// <summary>Returns <c>true</c> if the given app name is pinned at its current version.</summary>
    public bool IsPinned(string name, string? installedVersion)
    {
        if (_pins is null || !_pins.TryGetValue(name, out var entry))
        {
            return false;
        }

        // If pinned at a specific version, only suppress when installed matches.
        if (entry.Version is not null)
        {
            return string.Equals(entry.Version, installedVersion, StringComparison.OrdinalIgnoreCase);
        }

        // Pinned without a version = always suppress updates.
        return true;
    }

    /// <summary>Pins a package at its current version (or indefinitely when version is null).</summary>
    public async Task PinAsync(string name, string? version, CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        _pins![name] = new PinEntry
        {
            Name = name,
            Version = version,
            PinnedAt = DateTimeOffset.UtcNow
        };
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a pin for the specified package.</summary>
    public async Task UnpinAsync(string name, CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        _pins!.Remove(name);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(PinFilePath)!;
        Directory.CreateDirectory(dir);

        var data = new PinFile { Pins = _pins!.Values.ToArray() };
        await using var stream = File.Create(PinFilePath);
        await JsonSerializer.SerializeAsync(stream, data, PinJsonContext.Default.PinFile, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A single pinned package entry.</summary>
public sealed class PinEntry
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("pinned_at")]
    public DateTimeOffset PinnedAt { get; init; }
}

/// <summary>Root object for the pinned.json file.</summary>
public sealed class PinFile
{
    [JsonPropertyName("pins")]
    public IReadOnlyList<PinEntry> Pins { get; init; } = [];
}

[JsonSerializable(typeof(PinFile))]
[JsonSerializable(typeof(PinEntry))]
internal sealed partial class PinJsonContext : JsonSerializerContext;