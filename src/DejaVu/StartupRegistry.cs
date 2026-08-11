using Microsoft.Win32;

namespace DejaVu;

/// <summary>
/// Start-with-Windows, via HKCU\...\Run only — not the Startup folder, not Task Scheduler.
/// The registry is the single source of truth; nothing is mirrored into config.json, so
/// the checkbox stays honest if the value is removed by something else.
/// </summary>
internal static class StartupRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(AppInfo.Name) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySet(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                key.SetValue(AppInfo.Name, $"\"{AppInfo.ExecutablePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(AppInfo.Name, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
