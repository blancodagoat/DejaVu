namespace DejaVu;

/// <summary>
/// Opt-in background update notifications. Off by default, so "the app never phones
/// home" stays literally true until the user flips the tray toggle. When on: one
/// releases-API request shortly after launch and every six hours after — a new release
/// reaches running installs within hours instead of whenever someone remembers to
/// click, and four tiny requests a day sit far under GitHub's anonymous rate limit.
/// Each found version notifies once; failures are silent, because a blocked proxy or
/// an offline evening must not toast every cycle.
/// </summary>
/// <summary>A scoop install updates through scoop — handing those users a raw exe
/// download would orphan their package. Everyone else gets nudged toward scoop, so the
/// app becomes an *installed* thing with one-command updates instead of a loose exe.</summary>
internal static class ScoopInstall
{
    public static bool Active =>
        AppInfo.ExecutablePath.Contains(@"\scoop\apps\", StringComparison.OrdinalIgnoreCase);

    // Two commands on purpose: "scoop update <app>" alone compares against the
    // LOCAL bucket clone and happily reports a stale version as latest — the bare
    // "scoop update" first is what actually pulls the buckets.
    public static string UpdateCommand =>
        "scoop update; scoop update " + AppInfo.Name.ToLowerInvariant();

    /// <summary>
    /// Runs the whole update in a visible terminal and relaunches the app when done.
    /// The relaunch passes this pid as --takeover so the new instance waits out our
    /// shutdown instead of concluding "already running" and exiting. The caller must
    /// quit the app right after. The exe path survives the update: scoop swaps the
    /// "current" junction underneath it.
    /// </summary>
    public static void RunUpdateAndRelaunch()
    {
        var relaunch = $"Start-Process '{AppInfo.ExecutablePath}' -ArgumentList '--takeover {Environment.ProcessId}'";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "powershell.exe", $"-NoProfile -Command \"{UpdateCommand}; {relaunch}\"")
        {
            UseShellExecute = true,
        });
    }
}

internal sealed class UpdateNotifier : IDisposable
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly Func<bool> enabled;
    private readonly Action<Version, string> announce;
    private readonly System.Threading.Timer timer;
    private Version? announced;

    public UpdateNotifier(Func<bool> enabled, Action<Version, string> announce)
    {
        this.enabled = enabled;
        this.announce = announce;
        timer = new System.Threading.Timer(_ => Check(), null, FirstCheck, Interval);
    }

    private async void Check()
    {
        // async void is safe here: nothing below can throw past the catch.
        if (!enabled())
        {
            return;
        }

        try
        {
            var newer = await UpdateCheck.FindNewer(UpdateCheck.Current);
            if (newer is { } found && found.Version != announced)
            {
                announced = found.Version;
                announce(found.Version, found.Url);
            }
        }
        catch
        {
            // Silent by design; the manual menu check is the loud path.
        }
    }

    public void Dispose() => timer.Dispose();
}
