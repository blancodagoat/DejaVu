using Microsoft.Win32;

namespace DejaVu;

/// <summary>
/// Start-with-Windows, via HKCU\...\Run or — for an elevated install — a highest-run-level
/// scheduled task. Windows itself is the single source of truth; nothing is mirrored into
/// config.json, so the checkbox stays honest if the entry is removed by something else.
/// Exactly one of the two mechanisms may exist: both would race at every logon.
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
        // instead. Non-elevated goes back to the Run key.
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

        // The elevated task cannot be removed from here, and adding the Run value beside
        // it does not "double launch harmlessly": the two race at every logon, and when
        // the Run copy wins the mutex the session comes up NON-elevated — where Windows
        // withholds our hotkey under every elevated window (see Elevation), so saving in
        // an anti-cheat game silently stops working until the next reboot rolls the dice
        // the other way. Repair() runs on every launch, so one non-elevated start (a
        // manual launch after an update, or the moment before "Restart as administrator"
        // takes effect) used to poison an elevated install permanently. The task wins.
        if (TaskRunsThisExe())
        {
            DeleteRunValue();
            return true;
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

    private static bool TaskExists() => Schtasks($"/Query /TN {AppInfo.Name}", out _);

    /// <summary>
    /// A task left over from an exe that has since moved is worse than no task at all —
    /// Windows runs a missing target without a word — so it must not be trusted as the
    /// autostart. The verbose listing carries "Task To Run"; matching the path rather
    /// than the label keeps this working on non-English Windows.
    /// </summary>
    private static bool TaskRunsThisExe() =>
        Schtasks($"/Query /TN {AppInfo.Name} /FO LIST /V", out var listing)
        && listing.Contains(AppInfo.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    private static bool Schtasks(string arguments) => Schtasks(arguments, out _);

    private static bool Schtasks(string arguments, out string output)
    {
        output = string.Empty;
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });
            if (process is null)
            {
                return false;
            }

            // Drained before the wait: a query big enough to fill the pipe buffer would
            // block schtasks forever against a WaitForExit that never returns.
            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
