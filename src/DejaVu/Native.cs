using System.Runtime.InteropServices;

namespace DejaVu;

/// <summary>Win32 interop for hotkeys, the single-instance signal and the indicator overlay.</summary>
internal static class Native
{
    public const int WM_HOTKEY = 0x0312;

    /// <summary>Posted to the hotkey window by the single-instance watcher.</summary>
    public const int WM_APP_SHOW_SETTINGS = 0x0400 + 17;

    public static readonly IntPtr HWND_MESSAGE = new(-3);

    // hotkey modifiers
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    // extended styles for the indicator: no focus, no taskbar entry, clicks fall through
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOPMOST = 0x00000008;

    /// <summary>Keeps a window out of screen capture, so the dot never shows in saved replays.</summary>
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
}
