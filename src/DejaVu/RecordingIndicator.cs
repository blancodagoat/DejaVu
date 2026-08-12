namespace DejaVu;

/// <summary>
/// The tiny always-on-top dot in the corner that says the buffer is rolling — red while
/// buffering, dim while paused. Click-through, never activated, and excluded from screen
/// capture so it does not appear in saved replays.
/// </summary>
internal sealed class RecordingIndicator : Form
{
    private const int Diameter = 10;
    private const int IconSize = 16;
    private const int EdgeMargin = 12;

    private bool buffering = true;
    private bool useIcon;
    private Icon? appIcon;
    private string? targetDevice;

    public RecordingIndicator()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        // Magenta, not black: the app icon contains black pixels, and the key color
        // must never appear in what is drawn or it punches holes in it.
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        // Docking, resolution changes, and monitor unplugs used to strand the dot
        // off-screen — and a stranded dot reads as "the app died".
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        DpiChanged += (_, _) => Relayout();
        Relayout();
    }

    /// <summary>GDI device name of the display being captured; null follows the primary.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? TargetDevice
    {
        get => targetDevice;
        set
        {
            targetDevice = value;
            Relayout();
        }
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        // SystemEvents raises on its own thread; layout belongs to the UI thread.
        if (IsHandleCreated)
        {
            BeginInvoke(Relayout);
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Buffering
    {
        get => buffering;
        set
        {
            buffering = value;
            Invalidate();
        }
    }

    /// <summary>Draw the app icon instead of the red dot while buffering. Paused always dims to the dot.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool UseIcon
    {
        get => useIcon;
        set
        {
            if (value && appIcon is null)
            {
                try
                {
                    appIcon = Icon.ExtractAssociatedIcon(AppInfo.ExecutablePath);
                }
                catch
                {
                    // No icon to extract (odd hosting); the dot still works.
                }
            }

            useIcon = value && appIcon is not null;
            Relayout();
            Invalidate();
        }
    }

    /// <summary>Constants are 96-dpi values; a 10-px dot on a 200% laptop panel is
    /// effectively invisible.</summary>
    private int Scale(int value) => value * DeviceDpi / 96;

    private void Relayout()
    {
        int size = Scale(useIcon ? IconSize : Diameter);
        int margin = Scale(EdgeMargin);
        ClientSize = new Size(size, size);
        var screen = (targetDevice is null
                ? Screen.PrimaryScreen
                : Screen.AllScreens.FirstOrDefault(s => s.DeviceName == targetDevice) ?? Screen.PrimaryScreen);
        var area = screen?.WorkingArea ?? new Rectangle(0, 0, 800, 600);
        Location = new Point(area.Right - size - margin, area.Bottom - size - margin);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE
                | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Best-effort: on builds older than Win10 2004 the dot simply shows up in recordings.
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        int size = ClientSize.Width;
        if (useIcon && buffering && appIcon is not null)
        {
            e.Graphics.DrawIcon(appIcon, new Rectangle(0, 0, size, size));
            return;
        }

        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(buffering ? Color.FromArgb(220, 60, 60) : Theme.Dim);
        int dot = Scale(Diameter);
        e.Graphics.FillEllipse(brush, (size - dot) / 2, (size - dot) / 2, dot - 1, dot - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            appIcon?.Dispose();
        }

        base.Dispose(disposing);
    }
}
