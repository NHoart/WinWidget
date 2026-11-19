using System.Windows;

namespace CustomWidgets.WidgetProvider.Desktop;

internal static class DesktopHost
{
    public static void Run()
    {
        using var mutex = new Mutex(true, @"Local\CustomWidgets.DesktopHost", out bool createdNew);
        if (!createdNew)
        {
            Log.Info("Desktop host is already running; second instance exits.");
            return;
        }

        var thread = new Thread(RunUiThread)
        {
            IsBackground = false,
            Name = "CustomWidgets desktop host"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    private static void RunUiThread()
    {
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            var application = new Application
            {
                ShutdownMode = ShutdownMode.OnMainWindowClose
            };
            application.Run(new DesktopHostWindow());
        }
        catch (Exception ex)
        {
            Log.Error("Desktop host terminated unexpectedly.", ex);
        }
    }
}
