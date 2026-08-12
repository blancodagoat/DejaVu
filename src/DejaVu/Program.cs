namespace DejaVu;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Native.PinSystemDlls();

        using var instance = SingleInstance.Acquire();
        if (!instance.IsFirstInstance)
        {
            SingleInstance.SignalExisting();
            return;
        }

        ApplicationConfiguration.Initialize();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);

        using var context = new TrayContext(instance);
        Application.Run(context);
    }

    private static void Report(Exception? ex)
    {
        if (ex is null)
        {
            return;
        }

        MessageBox.Show(
            ex.Message, $"{AppInfo.Name} error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
