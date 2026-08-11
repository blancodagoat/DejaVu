using Microsoft.Win32;

namespace DejaVu;

/// <summary>
/// Tracks the shell light/dark setting so the tray glyph can contrast with the taskbar.
/// The change notification is WM_SETTINGCHANGE, surfaced here through SystemEvents —
/// a message-only window would not receive that broadcast.
/// </summary>
internal sealed class SystemTheme : IDisposable
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public event Action? Changed;

    private bool lightTaskbar;

    public SystemTheme()
    {
        lightTaskbar = ReadLightTaskbar();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>True when the shell chrome is light, i.e. the tray needs the dark glyph.</summary>
    public bool LightTaskbar => lightTaskbar;

    private static bool ReadLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
            // Absent means the default light theme.
            return key?.GetValue("SystemUsesLightTheme") is not int value || value != 0;
        }
        catch
        {
            return true;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle
            or UserPreferenceCategory.Color or UserPreferenceCategory.Window))
        {
            return;
        }

        var current = ReadLightTaskbar();
        if (current == lightTaskbar)
        {
            return;
        }

        lightTaskbar = current;
        Changed?.Invoke();
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
