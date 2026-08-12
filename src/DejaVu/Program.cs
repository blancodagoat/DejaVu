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
        // "runs from a game folder" (issue #2/#3) is invisible without the path.
        AppLog.Write($"startup: v{UpdateCheck.Current} from {AppInfo.ExecutablePath}");

        ApplicationConfiguration.Initialize();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);

        using var context = new TrayContext(instance);
        Application.Run(context);
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
