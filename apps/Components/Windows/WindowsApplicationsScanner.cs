using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace apps.Components.Windows;

/// <summary>
/// Discovers installed applications on Windows by enumerating the standard
/// Uninstall registry keys (HKLM/HKCU, 32-bit and 64-bit views).
/// The scanner emits a <see cref="DiscoveredApp"/> for each entry containing a
/// display name and optional version/path information. Update checking is not
/// implemented for Windows apps in this scanner — discovered apps are emitted
/// so they appear in inventory and can be checked by method-specific scanners.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsApplicationsScanner(ILogger<WindowsApplicationsScanner> logger) : IScanner
{
    private readonly ILogger<WindowsApplicationsScanner> _logger = logger;

    // No persistent state required; keep scanner lightweight.

    public string Name => "Applications";

    /// <inheritdoc/>
    public string DisplayName => "Applications";

    public OS SupportedOS => OS.Windows;
    public AppKind Kind => AppKind.App;

    /// <inheritdoc/>
    public bool IsAvailable()
    {
        // Windows registry uninstall keys exist on Windows systems; consider this
        // scanner available when any of the standard keys are present.
        try
        {
            // We'll track the presence of the HKLM/HKCU uninstall keys for logging/debugging.
            const string uninstallPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall";

            var hasAny = false;
            // HKLM 64-bit
            using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(uninstallPath))
            {
                if (key is not null)
                {
                    hasAny = true;
                }
            }

            // HKLM 32-bit
            using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(uninstallPath))
            {
                if (key is not null)
                {
                    hasAny = true;
                }
            }

            // HKCU 64-bit (per-user)
            using (var key = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64).OpenSubKey(uninstallPath))
            {
                if (key is not null)
                {
                    hasAny = true;
                }
            }

            // HKCU 32-bit
            using (var key = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32).OpenSubKey(uninstallPath))
            {
                if (key is not null)
                {
                    hasAny = true;
                }
            }

            return hasAny;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to probe Windows registry for installed applications");
            return false;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DiscoveredApp> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Enumerate the common uninstall registry locations (HKLM/HKCU, 64/32 views).
        const string uninstallPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall";

        var hives = new (RegistryHive Hive, RegistryView View, string Label)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM64"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32"),
            (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU64"),
            (RegistryHive.CurrentUser, RegistryView.Registry32, "HKCU32"),
        };

        foreach (var (hive, view, label) in hives)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RegistryKey? baseKey = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(uninstallPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Cannot open uninstall registry key {Label}", label);
            }

            if (baseKey is null)
            {
                continue;
            }

            foreach (var subName in baseKey.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();

                RegistryKey? sub = null;
                try
                {
                    sub = baseKey.OpenSubKey(subName);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Cannot open subkey {Entry} in {Label}", subName, label);
                    continue;
                }

                if (sub is null)
                {
                    continue;
                }

                // Read needed values into locals, then dispose the registry key before yielding
                string? displayName = null;
                string? displayVersion = null;
                string? installLocation = null;
                string? displayIcon = null;
                string? uninstallString = null;

                try
                {
                    displayName = sub.GetValue("DisplayName") as string;
                    displayVersion = sub.GetValue("DisplayVersion") as string;
                    installLocation = sub.GetValue("InstallLocation") as string;
                    displayIcon = sub.GetValue("DisplayIcon") as string;
                    uninstallString = sub.GetValue("UninstallString") as string;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Failed to read values for uninstall entry {Entry} in {Label}", subName, label);
                }
                finally
                {
                    try
                    {
                        sub.Dispose();
                    }
                    catch
                    {
                        // ignore
                    }
                }

                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                var path = installLocation ?? displayIcon ?? uninstallString;
                var name = Normalize(displayName);

                var app = new DiscoveredApp(this, name, new AppIdentifier(Name, DisplayName), AppKind.App)
                {
                    InstalledVersion = string.IsNullOrWhiteSpace(displayVersion) ? null : Normalize(displayVersion),
                    Path = string.IsNullOrWhiteSpace(path) ? null : path,
                    BundleId = subName,
                    Attribute = AppAttribute.App
                };

                yield return app;
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<(AppRecord App, bool Error)> CheckAsync(AppRecord[] apps, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Windows scanner does not implement remote update checks. Emit every app as
        // checked with no error so it appears in the final output (up-to-date info is
        // unresolved unless another checker supplements it).
        foreach (var record in apps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return (record, false);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static string Normalize(string? value)
    {
        return value?.Trim().Normalize(System.Text.NormalizationForm.FormC).Replace("\u200E", string.Empty) ?? string.Empty;
    }
}