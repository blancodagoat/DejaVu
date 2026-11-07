namespace DejaVu;

/// <summary>Flat dark rendering for the tray context menu, matching the settings window.</summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer()
        : base(new DarkColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item is { Enabled: false })
        {
            // The base renderer greys disabled text with the system GrayText, 3.6:1 on
            // this background: too faint for the Audio devices note line. The theme's
            // dim tone still reads as greyed and clears 4.5:1.
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, Theme.Dim, e.TextFormat);
            return;
        }

        e.TextColor = e.Item?.Selected == true ? Theme.Accent : Theme.Text;
        base.OnRenderItemText(e);
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Background;

        public override Color MenuItemSelected => Theme.FieldHover;

        public override Color MenuItemSelectedGradientBegin => Theme.FieldHover;

        public override Color MenuItemSelectedGradientEnd => Theme.FieldHover;

        public override Color MenuItemBorder => Theme.Border;

        public override Color MenuBorder => Theme.Border;

        public override Color ImageMarginGradientBegin => Theme.Background;

        public override Color ImageMarginGradientMiddle => Theme.Background;

        public override Color ImageMarginGradientEnd => Theme.Background;

        public override Color SeparatorDark => Theme.Border;

        public override Color SeparatorLight => Theme.Border;
    }
}
