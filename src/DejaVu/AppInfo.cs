namespace DejaVu;

internal static class AppInfo
{
    public const string Name = "DejaVu";
    public const string GitHubUrl = "https://github.com/blancodagoat/dejavu";

    /// <summary>
    /// Full path of the running executable. ProcessPath is the only reliable source under
    /// single-file publish, where Assembly.Location is empty.
    /// </summary>
    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, Name + ".exe");

    /// <summary>%APPDATA%\DejaVu — config.json lives here.</summary>
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);

    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    /// <summary>
    /// The rolling segment ring. LocalAppData rather than %TEMP% so disk cleaners do not
    /// yank segments out from under an active buffer.
    /// </summary>
    public static string BufferDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name, "buffer");

    public static string DefaultSaveRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), Name);
}
