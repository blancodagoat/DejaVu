namespace DejaVu;

/// <summary>
/// Hidden message-only window that owns the global hotkey registration and
/// receives WM_HOTKEY. Message-only windows still receive posted messages, which
/// is all RegisterHotKey and the single-instance signal need.
/// </summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    public event Action<HotkeyId>? HotkeyPressed;
    public event Action? ShowSettingsRequested;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = AppInfo.Name + ".HotkeyWindow",
            Parent = Native.HWND_MESSAGE,
        });
    }

    public bool Register(HotkeyId id, HotkeyBinding binding)
    {
        if (!binding.IsValid)
        {
            return false;
        }

        Native.UnregisterHotKey(Handle, (int)id);
        return Native.RegisterHotKey(Handle, (int)id, binding.Modifiers, binding.VirtualKey);
    }

    public void Unregister(HotkeyId id) => Native.UnregisterHotKey(Handle, (int)id);

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_HOTKEY:
                HotkeyPressed?.Invoke((HotkeyId)m.WParam.ToInt32());
                return;

            case Native.WM_APP_SHOW_SETTINGS:
                ShowSettingsRequested?.Invoke();
                return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            Native.UnregisterHotKey(Handle, (int)HotkeyId.Save);
            DestroyHandle();
        }
    }
}
