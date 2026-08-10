using Microsoft.Win32;

namespace Cursivis.Windows.Platform.Startup;

public sealed class WindowsStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Must match installer/Cursivis.iss and remain distinct from legacy
    // "CursivisHotkeyHost" so Next never steals or clears the older product.
    private const string ValueName = "Cursivis Next";
    private const string LegacyValueName = "Cursivis";

    public bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return HasStartupCommand(key, ValueName) || HasStartupCommand(key, LegacyValueName);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows startup registration requires Windows.");
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new UnauthorizedAccessException("The current-user startup registry key is unavailable.");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            // Remove the pre-isolation value name so Settings can fully disable
            // launch-at-sign-in on machines that still carry it.
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            return;
        }

        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The Cursivis executable path is unavailable.");
        key.SetValue(
            ValueName,
            $"\"{executable}\" --background",
            RegistryValueKind.String);
        // Prefer the installer-aligned value name exclusively.
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    private static bool HasStartupCommand(RegistryKey? key, string valueName) =>
        key?.GetValue(valueName) is string command && !string.IsNullOrWhiteSpace(command);
}
