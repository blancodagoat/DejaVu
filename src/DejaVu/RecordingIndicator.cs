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
        Relayout();
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

    private void Relayout()
    {
        int size = useIcon ? IconSize : Diameter;
        ClientSize = new Size(size, size);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 800, 600);
        Location = new Point(area.Right - size - EdgeMargin, area.Bottom - size - EdgeMargin);
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
        if (useIcon && buffering && appIcon is not null)
        {
            e.Graphics.DrawIcon(appIcon, new Rectangle(0, 0, IconSize, IconSize));
            return;
        }

        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(buffering ? Color.FromArgb(220, 60, 60) : Theme.Dim);
        int size = ClientSize.Width;
        e.Graphics.FillEllipse(brush, (size - Diameter) / 2, (size - Diameter) / 2, Diameter - 1, Diameter - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            appIcon?.Dispose();
        }

        base.Dispose(disposing);
    }
}
