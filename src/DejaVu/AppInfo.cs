namespace DejaVu;

internal static class AppInfo
{
    public const string Name = "DejaVu";
    public const string GitHubUrl = "https://github.com/blancodagoat/dejavu";

    private static string? renamedTo;

    /// <summary>SelfTidy renamed the running exe; ProcessPath keeps reporting the old
    /// name, and autostart repair would re-point at a file that no longer exists.</summary>
    public static void NoteRenamed(string path) => renamedTo = path;

    /// <summary>
    /// Full path of the running executable. ProcessPath is the only reliable source under
    /// single-file publish, where Assembly.Location is empty.
    /// </summary>
    public static string ExecutablePath =>
        renamedTo ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, Name + ".exe");

    /// <summary>
    /// GetFolderPath returns "" when a known folder is unregistered (stripped images,
    /// broken profiles) — and Path.Combine("", "DejaVu") is a RELATIVE path that
    /// resolves against the CWD, which for autostart is C:\Windows\System32. Fall back
    /// to living beside the exe, portable-app style, rather than that.
    /// </summary>
    private static string Known(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        return path.Length > 0 ? path : Path.GetDirectoryName(ExecutablePath)!;
    }

    /// <summary>%APPDATA%\DejaVu — config.json lives here.</summary>
    public static string DataDirectory => Path.Combine(Known(Environment.SpecialFolder.ApplicationData), Name);

    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    /// <summary>
    /// The rolling segment ring. LocalAppData rather than %TEMP% so disk cleaners do not
    /// yank segments out from under an active buffer. The env override exists for the
    /// test harness: a test sharing the live app's ring made both prune and mux each
    /// other's half-written segments.
    /// </summary>
    public static string BufferDirectory =>
        Environment.GetEnvironmentVariable("DEJAVU_BUFFER_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Known(Environment.SpecialFolder.LocalApplicationData), Name, "buffer");

    public static string DefaultSaveRoot => Path.Combine(Known(Environment.SpecialFolder.MyVideos), Name);
}
