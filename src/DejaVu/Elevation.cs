using System.Diagnostics;
using System.Security.Principal;

namespace DejaVu;

/// <summary>
/// Windows does not deliver a non-elevated app's registered hotkeys while an elevated
/// window has focus (games with anti-cheat, admin terminals, Task Manager). The bypass
/// is to run elevated ourselves, opted into by the user from the tray menu.
/// </summary>
internal static class Elevation
{
    public static bool IsElevated { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>
    /// Relaunches this exe through UAC. Returns false when the prompt is declined.
    /// On success the caller must exit promptly so the new instance can take the mutex.
    /// </summary>
    public static bool TryRestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppInfo.ExecutablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
