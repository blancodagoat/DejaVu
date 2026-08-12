using System.Runtime.InteropServices;

namespace DejaVu;

/// <summary>Win32 interop for hotkeys, the single-instance signal and the indicator overlay.</summary>
internal static class Native
{
    /// <summary>
    /// Pins d3d11/dxgi imports to the System32 copies. Game folders often hold shim
    /// versions of these DLLs (ReShade, dxvk, ENB), and the default search order loads
    /// them from the exe's own folder first — the shim lacks the WinRT interop exports
    /// and capture dies with EntryPointNotFound (issue #2). Call before any capture.
    /// </summary>
    public static void PinSystemDlls()
    {
        // The resolver below only covers this assembly's own P/Invokes. Dependency
        // resolution is the other half (issue #3): when the pinned System32 d3d11.dll
        // loads, the loader resolves its dxgi.dll import by normal search order and
        // still picks up a ReShade shim beside the exe, which then proxies DXGI inside
        // our process and breaks Media Foundation with E_NOINTERFACE. System32-only
        // default directories close that path process-wide; the runtime's own natives
        // are unaffected because the host loads them by full path.
        SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32);

        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) =>
            name.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase)
            || name.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase)
                ? NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, name))
                : IntPtr.Zero);
    }

    private const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint flags);

    public const int WM_HOTKEY = 0x0312;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYUP = 0x0105;
    public const int VK_SNAPSHOT = 0x2C;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

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

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    /// <summary>True while the Windows key is physically held down.</summary>
    public static bool IsWinKeyDown() =>
        (GetKeyState(VK_LWIN) & 0x8000) != 0 || (GetKeyState(VK_RWIN) & 0x8000) != 0;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    /// <summary>Best-effort dark title bar. Attribute 20 on current builds, 19 on older Win10.</summary>
    public static void TryUseDarkTitleBar(IntPtr hWnd)
    {
        int on = 1;
        if (DwmSetWindowAttribute(hWnd, 20, ref on, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hWnd, 19, ref on, sizeof(int));
        }
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>Walks child windows until the callback returns false.</summary>
    public static void EnumChildWindows(IntPtr parent, Func<IntPtr, bool> visit)
    {
        EnumChildWindows(parent, (hwnd, _) => visit(hwnd), IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, uint devNum, ref DISPLAY_DEVICE displayDevice, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public uint dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    /// <summary>The highest refresh rate any attached display is currently running at.</summary>
    public static int MaxDisplayRefresh()
    {
        int max = 60;
        try
        {
            var device = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (uint i = 0; EnumDisplayDevicesW(null, i, ref device, 0); i++)
            {
                var mode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
                if (EnumDisplaySettingsW(device.DeviceName, ENUM_CURRENT_SETTINGS, ref mode)
                    && mode.dmDisplayFrequency > max)
                {
                    max = (int)mode.dmDisplayFrequency;
                }
            }
        }
        catch
        {
            // 60 is always a safe answer.
        }

        return max;
    }

    /// <summary>GDI device name (\\.\DISPLAY1) of the monitor hosting the foreground window.</summary>
    public static string? ForegroundDisplayDevice()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        return monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info) ? info.szDevice : null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    public static IntPtr PrimaryMonitor() => MonitorFromPoint(default, 1 /* DEFAULTTOPRIMARY */);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    /// <summary>The HMONITOR currently backing a GDI device name, or zero if unplugged.</summary>
    public static IntPtr MonitorFromDeviceName(string deviceName)
    {
        IntPtr found = IntPtr.Zero;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(monitor, ref info)
                && string.Equals(info.szDevice, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                found = monitor;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Attached displays as (GDI device name, human monitor name).</summary>
    public static List<(string DeviceName, string FriendlyName)> ListDisplays()
    {
        var result = new List<(string, string)>();
        var adapter = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref adapter, 0); i++)
        {
            const uint AttachedToDesktop = 0x1;
            if ((adapter.StateFlags & AttachedToDesktop) == 0)
            {
                continue;
            }

            var monitor = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
            var friendly = EnumDisplayDevicesW(adapter.DeviceName, 0, ref monitor, 0)
                ? monitor.DeviceString
                : adapter.DeviceString;
            result.Add((adapter.DeviceName, friendly));
        }

        return result;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    /// <summary>Visible, titled, non-minimised top-level windows for the capture picker.</summary>
    public static List<(IntPtr Handle, string Title)> ListWindows()
    {
        var result = new List<(IntPtr, string)>();
        var sb = new System.Text.StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && !IsIconic(hwnd))
            {
                sb.Clear();
                if (GetWindowTextW(hwnd, sb, sb.Capacity) > 0)
                {
                    result.Add((hwnd, sb.ToString()));
                }
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }
}
