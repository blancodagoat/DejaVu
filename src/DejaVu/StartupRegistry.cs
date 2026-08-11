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

    /// <summary>
    /// Call once at launch. The Run entry stores an absolute path, so moving, renaming
    /// or re-downloading the exe silently breaks it — Windows skips missing startup
    /// targets without a word. Rewriting the enabled entry with wherever the exe lives
    /// right now keeps it working from any location, and migrates the mechanism when
    /// the elevation state has changed.
    /// </summary>
    public static void Repair()
    {
        if (IsEnabled())
        {
            TrySet(true);
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            if (key?.GetValue(AppInfo.Name) is string value && value.Length > 0)
            {
                return true;
            }
        }
        catch
        {
            // Fall through to the task check.
        }

        return TaskExists();
    }

    public static bool TrySet(bool enabled)
    {
        if (!enabled)
        {
            DeleteRunValue();
            // Deleting a highest-level task needs elevation; from a normal process this
            // quietly fails and IsEnabled stays honest about it.
            Schtasks($"/Delete /F /TN {AppInfo.Name}");
            return !IsEnabled();
        }

        // Windows never launches elevated apps from the Run key — it skips them without
        // a word — so an elevated install rides a highest-run-level scheduled task
        // instead. Non-elevated goes back to the Run key; if an old task lingers and
        // cannot be removed, the single-instance mutex makes the double launch harmless.
        if (Elevation.IsElevated)
        {
            bool made = Schtasks(
                $"/Create /F /RL HIGHEST /SC ONLOGON /TN {AppInfo.Name} /TR \"\\\"{AppInfo.ExecutablePath}\\\"\"");
            if (made)
            {
                DeleteRunValue();
            }

            return made;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue(AppInfo.Name, $"\"{AppInfo.ExecutablePath}\"", RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(AppInfo.Name, throwOnMissingValue: false);
        }
        catch
        {
            // Nothing to delete, or no access; IsEnabled reports whatever remains.
        }
    }

    private static bool TaskExists() => Schtasks($"/Query /TN {AppInfo.Name}");

    private static bool Schtasks(string arguments)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
