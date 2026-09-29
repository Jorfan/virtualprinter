using VirtualPOSPrinter.UI;

namespace VirtualPOSPrinter;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Services.AppLog.Error("Unhandled UI error: " + e.Exception);
        Application.Run(new MainForm(args.Contains("--minimized")));
    }
}
