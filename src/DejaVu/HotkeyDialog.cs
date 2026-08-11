namespace DejaVu;

/// <summary>
/// The one dialog in the app: click the field, press the new save-hotkey combination.
/// Applies immediately on a successful registration; a refused key reverts on the spot.
/// </summary>
internal sealed class HotkeyDialog : Form
{
    public HotkeyDialog(
        HotkeyBinding current,
        Func<HotkeyBinding, bool> apply,
        Action<bool> suspendHotkey)
    {
        Text = "Save hotkey";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Ui(9.5f);
        ClientSize = new Size(320, 108);
        Icon = TrayIcons.CreateWindowIcon();

        var box = new HotkeyBox
        {
            Binding = current,
            Bounds = new Rectangle(16, 16, 288, 32),
            Recorded = apply,
            RecordingChanged = suspendHotkey,
        };

        var hint = new Label
        {
            Text = "Click the field, press the new combination. Esc cancels.",
            ForeColor = Theme.Dim,
            Bounds = new Rectangle(16, 58, 288, 40),
            Font = Theme.Ui(8.5f),
        };

        Controls.Add(box);
        Controls.Add(hint);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.TryUseDarkTitleBar(Handle);
    }
}
