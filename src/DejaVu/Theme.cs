using System.Drawing.Text;

namespace DejaVu;

/// <summary>The dark palette shared by the settings window, the tray menu and the overlay.</summary>
internal static class Theme
{
    public static readonly Color Background = ColorTranslator.FromHtml("#12100e");
    public static readonly Color Text = ColorTranslator.FromHtml("#f2ece2");
    public static readonly Color Accent = ColorTranslator.FromHtml("#e0913f");
    public static readonly Color Warning = ColorTranslator.FromHtml("#e0563f");
    public static readonly Color Dim = ColorTranslator.FromHtml("#8b837a");
    public static readonly Color Field = ColorTranslator.FromHtml("#1c1916");
    public static readonly Color FieldHover = ColorTranslator.FromHtml("#26211b");
    public static readonly Color Border = ColorTranslator.FromHtml("#2a251f");

    /// <summary>
    /// Segoe UI Variable ships with Windows 11 only, so Windows 10 22H2 falls back to Segoe UI.
    /// </summary>
    private static readonly string Family = ResolveFamily(
        "Segoe UI Variable Text", "Segoe UI", "Tahoma");

    public static Font Ui(float points, FontStyle style = FontStyle.Regular) =>
        new(Family, points, style, GraphicsUnit.Point);

    private static string ResolveFamily(params string[] candidates)
    {
        try
        {
            using var installed = new InstalledFontCollection();
            var names = new HashSet<string>(
                installed.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                if (names.Contains(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // Font enumeration is best-effort; the generic sans fallback below is always valid.
        }

        return FontFamily.GenericSansSerif.Name;
    }
}
