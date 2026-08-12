namespace DejaVu;

/// <summary>
/// A small rolling text log — balloons vanish and games hide them, and a field failure
/// with no trace cannot be debugged. Kept under ~256 KB by halving when it grows past.
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string PathName = Path.Combine(AppInfo.DataDirectory, "log.txt");

    /// <summary>The last chunk of the log, cut at a line break, for issue reports.</summary>
    public static string Tail(int maxChars)
    {
        lock (Gate)
        {
            try
            {
                var text = File.ReadAllText(PathName);
                if (text.Length > maxChars)
                {
                    text = text[^maxChars..];
                    int firstBreak = text.IndexOf('\n');
                    text = firstBreak >= 0 ? text[(firstBreak + 1)..] : text;
                }

                return text.Trim();
            }
            catch
            {
                return "(no log)";
            }
        }
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppInfo.DataDirectory);
                File.AppendAllText(PathName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");

                var info = new FileInfo(PathName);
                if (info.Length > 256 * 1024)
                {
                    var text = File.ReadAllText(PathName);
                    File.WriteAllText(PathName, text[(text.Length / 2)..]);
                }
            }
            catch
            {
                // Logging must never hurt the app.
            }
        }
    }
}
