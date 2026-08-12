using System.Diagnostics;

namespace DejaVu;

/// <summary>
/// Failure reporting that keeps "never phones home" literally true: the app only builds
/// a prefilled new-issue URL and hands it to the browser, where the user reads exactly
/// what would be sent and submits it themselves — or closes the tab. Offered only from
/// failure balloons, never on a timer and never on first run.
/// </summary>
internal static class IssueReport
{
    // GitHub 414s somewhere past 8 KB of URL; the encoded log tail is the bulk of it.
    private const int MaxLogChars = 1800;

    public static void Open(string context)
    {
        try
        {
            var environment =
                $"v{UpdateCheck.Current} · Windows {Environment.OSVersion.Version} · " +
                $"AV1 hw: {(CaptureEngine.Av1Available ? "yes" : "no")}";
            var url = BuildUrl(context, environment, AppLog.Tail(MaxLogChars),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Reporting must never hurt the app.
        }
    }

    /// <summary>Pure so the tests can assert on it; Open supplies the live values.</summary>
    public static string BuildUrl(
        string context, string environment, string logTail, string userProfile, string userName)
    {
        if (logTail.Length > MaxLogChars)
        {
            logTail = logTail[^MaxLogChars..];
        }

        var body =
            $"**What happened:** {context}\n\n" +
            $"**Environment:** {environment}\n\n" +
            "**Log tail:**\n```\n" + Scrub(logTail, userProfile, userName) + "\n```\n";

        return $"{AppInfo.GitHubUrl}/issues/new" +
            $"?title={Uri.EscapeDataString($"{context} (from the app)")}" +
            $"&body={Uri.EscapeDataString(body)}";
    }

    /// <summary>Log lines carry paths; paths carry the username. Neither belongs in a public issue.</summary>
    public static string Scrub(string text, string userProfile, string userName)
    {
        if (userProfile.Length > 0)
        {
            text = text.Replace(userProfile, "~", StringComparison.OrdinalIgnoreCase);
        }

        if (userName.Length > 0)
        {
            text = text.Replace(userName, "<user>", StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }
}
