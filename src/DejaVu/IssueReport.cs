using System.Diagnostics;

namespace DejaVu;

/// <summary>
/// Failure reporting that keeps "never phones home" literally true: the app copies a short
/// report (what happened, the environment, a scrubbed log tail) to the clipboard and opens
/// the DejaVu Discord, where the user reads it and pastes it in #support themselves, or
/// doesn't. Offered only from failure balloons, never on a timer and never on first run.
/// </summary>
internal static class IssueReport
{
    // A Discord message holds 2000 characters; the report around the log tail takes ~250.
    private const int MaxLogChars = 1400;
    public const int MaxReportChars = 2000;

    /// <summary>Copies the report and opens the invite. True when the copy worked. Runs on
    /// the tray's UI thread, which is STA, as the clipboard needs.</summary>
    public static bool Open(string context)
    {
        bool copied = false;
        try
        {
            var environment =
                $"v{UpdateCheck.Current} · Windows {Environment.OSVersion.Version} · " +
                $"AV1 hw: {(CaptureEngine.Av1Available ? "yes" : "no")}" +
                (CaptureEngine.Av1Available && CaptureEngine.Av1.Rejected ? " (off this session)" : string.Empty);
            var report = BuildReport(context, environment, AppLog.Tail(MaxLogChars),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName);
            try
            {
                Clipboard.SetText(report);
                copied = true;
            }
            catch
            {
                // Another app holding the clipboard: the invite still opens.
            }

            Process.Start(new ProcessStartInfo(AppInfo.SupportInvite) { UseShellExecute = true });
        }
        catch
        {
            // Reporting must never hurt the app.
        }

        return copied;
    }

    /// <summary>Pure so the tests can assert on it; Open supplies the live values. Always
    /// fits one Discord message.</summary>
    public static string BuildReport(
        string context, string environment, string logTail, string userProfile, string userName)
    {
        if (logTail.Length > MaxLogChars)
        {
            logTail = logTail[^MaxLogChars..];
        }

        const string nl = "\n";
        const string fence = "```";
        var report =
            $"**What happened:** {context}" + nl +
            $"**Environment:** {environment}" + nl +
            "**Log tail:**" + nl + fence + nl + Scrub(logTail, userProfile, userName) + nl + fence;
        return report.Length <= MaxReportChars ? report : report[..(MaxReportChars - 4)] + nl + fence;
    }

    /// <summary>Log lines carry paths; paths carry the username. Neither belongs in a public issue.</summary>
    public static string Scrub(string text, string userProfile, string userName)
    {
        if (userProfile.Length > 0)
        {
            text = text.Replace(userProfile, "~", StringComparison.OrdinalIgnoreCase);
        }

        // Word-bounded, and only for names that are unlikely to be ordinary words:
        // a username like "PC" or "user" used to shred every unrelated occurrence in
        // the log. The profile-path replace above already caught the path form.
        if (userName.Length >= 3)
        {
            text = System.Text.RegularExpressions.Regex.Replace(
                text, $@"\b{System.Text.RegularExpressions.Regex.Escape(userName)}\b", "<user>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return text;
    }
}
