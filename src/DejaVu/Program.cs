namespace DejaVu;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Native.PinSystemDlls();
        WaitForTakeover(args);

        using var instance = SingleInstance.Acquire();
        if (!instance.IsFirstInstance)
        {
            SingleInstance.SignalExisting();
            return;
        }

        SelfTidy.Run();

        // Version and location up front: log tails in issue reports span updates, and
        // "runs from a game folder" (issue #2/#3) is invisible without the path. The
        // elevation state belongs here too — "the hotkey does nothing in game" is this
        // line reading "not elevated", and nothing else in the log gives it away.
        AppLog.Write($"startup: v{UpdateCheck.Current} "
            + $"{(Elevation.IsElevated ? "elevated" : "not elevated")} from {AppInfo.ExecutablePath}");

        ApplicationConfiguration.Initialize();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);

        using (var context = new TrayContext(instance))
        {
            Application.Run(context);
            AppLog.Write("exiting");

            // Shutting down flushes the last segment through Media Foundation and the
            // audio device, and either can wedge on a dead device: the tray icon vanished
            // and the process lived on until Task Manager (issue #4). The flush gets a
            // bounded window — inside the 15 s a --takeover relaunch waits — and then the
            // process goes regardless: losing the tail of one segment beats a zombie
            // holding the single-instance mutex against the next launch. Disposal happens
            // at the closing brace, under this watchdog.
            new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(10));
                AppLog.Write("shutdown timed out; forcing exit");
                Environment.Exit(1);
            })
            { IsBackground = true, Name = "DejaVu shutdown watchdog" }.Start();
        }

        // A leftover foreground thread would keep the process alive with no tray icon
        // left to close it; nothing above needs to outlive this point.
        Environment.Exit(0);
    }

    /// <summary>"Restart as administrator" hands us the old instance's pid: it needs
    /// seconds to finalize its in-flight segment, and grabbing the single-instance
    /// mutex before it dies used to end the relaunch as "already running".</summary>
    private static void WaitForTakeover(string[] args)
    {
        if (args is ["--takeover", var pidText] && int.TryParse(pidText, out int pid))
        {
            try
            {
                System.Diagnostics.Process.GetProcessById(pid).WaitForExit(15000);
            }
            catch
            {
                // Already gone — exactly what we were waiting for.
            }
        }
    }

    private static void Report(Exception? ex)
    {
        if (ex is null)
        {
            return;
        }

        AppLog.Write("unhandled: " + ex);
        MessageBox.Show(
            ex.Message, $"{AppInfo.Name} error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
