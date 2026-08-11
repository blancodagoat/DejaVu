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
