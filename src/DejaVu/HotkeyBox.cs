using System.ComponentModel;

namespace DejaVu;

/// <summary>
/// Click-to-record hotkey field. Clicking arms it, the next non-modifier keypress is
/// captured, Esc cancels the recording.
/// </summary>
internal sealed class HotkeyBox : Control
{
    private HotkeyBinding binding;
    private bool recording;

    /// <summary>
    /// Raised with a candidate binding. The handler applies it and returns false if
    /// Windows refused the registration, in which case the field reverts.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<HotkeyBinding, bool>? Recorded { get; set; }

    /// <summary>True while armed. The owner suspends global hotkeys for the duration.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<bool>? RecordingChanged { get; set; }

    public HotkeyBox()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable,
            true);

        TabStop = true;
        Cursor = Cursors.Hand;
        BackColor = Theme.Field;
        ForeColor = Theme.Text;
        Font = Theme.Ui(9.5f);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HotkeyBinding Binding
    {
        get => binding;
        set
        {
            binding = value;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        BeginRecording();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        CancelRecording();
    }

    private void BeginRecording()
    {
        if (recording)
        {
            return;
        }

        recording = true;
        RecordingChanged?.Invoke(true);
        Invalidate();
    }

    private void CancelRecording()
    {
        if (!recording)
        {
            return;
        }

        recording = false;
        RecordingChanged?.Invoke(false);
        Invalidate();
    }

    /// <summary>While armed the field swallows every key, including Tab and Alt combinations.</summary>
    protected override bool IsInputKey(Keys keyData) => recording || base.IsInputKey(keyData);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!recording)
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        var key = keyData & Keys.KeyCode;

        if (key == Keys.Escape)
        {
            CancelRecording();
            return true;
        }

        if (HotkeyBinding.IsModifier(key))
        {
            return true;
        }

        Commit(key);
        return true;
    }

    /// <summary>
    /// Print Screen never produces a WM_KEYDOWN, so it would be unrecordable through
    /// ProcessCmdKey alone. It is picked up on key-up instead.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (recording && m.Msg is Native.WM_KEYUP or Native.WM_SYSKEYUP
            && m.WParam.ToInt32() == Native.VK_SNAPSHOT)
        {
            Commit(Keys.PrintScreen);
            return;
        }

        base.WndProc(ref m);
    }

    private void Commit(Keys key)
    {
        var modifiers = ModifierKeys;
        var candidate = new HotkeyBinding(
            key,
            (modifiers & Keys.Control) != 0,
            (modifiers & Keys.Alt) != 0,
            (modifiers & Keys.Shift) != 0,
            Native.IsWinKeyDown());

        recording = false;

        // Re-register (Resume) before the rebind runs, so TryRebind's unregister/register
        // sequence starts from the normal registered state.
        RecordingChanged?.Invoke(false);

        if (candidate.IsValid && Recorded?.Invoke(candidate) == true)
        {
            binding = candidate;
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(recording ? Theme.FieldHover : Theme.Field);

        using var border = new Pen(recording || Focused ? Theme.Accent : Theme.Border, 1f);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        var text = recording ? "Press a key…" : binding.ToString();
        using var brush = new SolidBrush(recording ? Theme.Accent : Theme.Text);
        using var format = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        var bounds = new Rectangle(9, 0, Width - 18, Height);
        g.DrawString(text, Font, brush, bounds, format);
    }
}
