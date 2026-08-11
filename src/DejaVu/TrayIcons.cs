namespace DejaVu;

/// <summary>
/// Loads the embedded multi-resolution icons. They are embedded rather than shipped
/// alongside so the single-file build stays a single file.
/// </summary>
internal static class TrayIcons
{
    private static Icon? light;
    private static Icon? dark;

    /// <summary>
    /// Picks the glyph that contrasts with the taskbar: the pale mark on dark chrome,
    /// the near-black mark on light chrome. The returned instances are cached and shared,
    /// so callers must not dispose them.
    /// </summary>
    public static Icon ForTaskbar(bool lightTaskbar) => lightTaskbar ? Dark : Light;

    /// <summary>
    /// A fresh window icon per call. Forms dispose the icon they are given, so handing
    /// out the cached tray instances here would leave the tray with a dead handle.
    /// </summary>
    public static Icon CreateWindowIcon() =>
        Load("DejaVu.app.ico", SystemInformation.IconSize);

    private static Icon Light => light ??= Load("DejaVu.tray-light.ico", SystemInformation.SmallIconSize);

    private static Icon Dark => dark ??= Load("DejaVu.tray-dark.ico", SystemInformation.SmallIconSize);

    private static Icon Load(string resourceName, Size size)
    {
        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return SystemIcons.Application;
        }

        // Asking for a specific size picks the matching frame out of the .ico instead of
        // downsampling the 256px one.
        return new Icon(stream, size);
    }
}
