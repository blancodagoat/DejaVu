namespace DejaVu;

/// <summary>
/// The tiny always-on-top dot in the corner that says the buffer is rolling — red while
/// buffering, dim while paused. Click-through, never activated, and excluded from screen
/// capture so it does not appear in saved replays.
/// </summary>
internal sealed class RecordingIndicator : Form
{
    private const int Diameter = 10;
    private const int EdgeMargin = 12;

    private bool buffering = true;

    public RecordingIndicator()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Black;
        TransparencyKey = Color.Black;
        ClientSize = new Size(Diameter, Diameter);

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 800, 600);
        Location = new Point(area.Right - Diameter - EdgeMargin, area.Bottom - Diameter - EdgeMargin);
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
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(buffering ? Color.FromArgb(220, 60, 60) : Theme.Dim);
        e.Graphics.FillEllipse(brush, 0, 0, Diameter - 1, Diameter - 1);
    }
}
