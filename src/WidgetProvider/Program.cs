using System.Runtime.InteropServices;
using CustomWidgets.WidgetProvider.Com;
using CustomWidgets.WidgetProvider.Desktop;

namespace CustomWidgets.WidgetProvider;

/// <summary>
/// Entry point for the packaged, windowless widget provider. The Widgets Board
/// activates the process through the COM server declared in Package.appxmanifest.
/// </summary>
internal static class Program
{
    private const uint ClsctxLocalServer = 0x4;
    private const uint RegclsMultipleUse = 0x1;

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
        uint dwClsContext,
        uint flags,
        out uint lpdwRegister);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint dwRegister);

    [MTAThread]
    private static void Main(string[] args)
    {
        // COM activation normally passes -RegisterProcessAsComServer because the
        // manifest declares Arguments on com:ExeServer. -Embedding is accepted as
        // a fallback. A normal launch (including the startup task) starts the
        // desktop host on an STA thread instead of registering the COM server.
        bool desktopHost = IsDesktopActivation(args);
        if (!desktopHost && !IsComActivation(args))
        {
            return;
        }

        Log.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception in widget provider.", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception in widget provider.", e.Exception);
            e.SetObserved();
        };

        if (desktopHost)
        {
            Log.Info("Starting desktop host.");
            DesktopHost.Run();
            return;
        }

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();

            var factory = new WidgetProviderFactory<WidgetProvider>();
            int registerResult = CoRegisterClassObject(
                WidgetProvider.ClassId,
                factory,
                ClsctxLocalServer,
                RegclsMultipleUse,
                out uint cookie);

            if (registerResult < 0)
            {
                Marshal.ThrowExceptionForHR(registerResult);
            }

            try
            {
                WidgetProvider.Initialize();

                // Stay alive while widgets are pinned. Exiting when the last widget
                // is removed is the lifetime policy recommended by the widget docs.
                while (true)
                {
                    if (WidgetProvider.WaitForNoWidgets(TimeSpan.FromMinutes(2)) &&
                        !WidgetProvider.HasWidgets)
                    {
                        break;
                    }

                    if (!WidgetProvider.HasWidgets)
                    {
                        Log.Info("No widgets are pinned anymore; provider exits.");
                        break;
                    }
                }
            }
            finally
            {
                int revokeResult = CoRevokeClassObject(cookie);
                if (revokeResult < 0)
                {
                    Log.Warn($"CoRevokeClassObject failed: 0x{revokeResult:X8}");
                }

                GC.KeepAlive(factory);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Widget provider terminated unexpectedly.", ex);
        }
    }

    private static bool IsDesktopActivation(string[] args)
    {
        if (args.Length == 0)
        {
            return true;
        }

        foreach (string arg in args)
        {
            if (string.Equals(arg, "-DesktopHost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsComActivation(string[] args)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, "-RegisterProcessAsComServer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-Embedding", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
