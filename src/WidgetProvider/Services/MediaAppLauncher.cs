using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CustomWidgets.WidgetProvider.Services;

/// <summary>
/// Opens the app that owns the current media session. GSMTC exposes that app as
/// an AppUserModelId. Win32 players such as Chrome use an AUMID that the shell
/// activation manager cannot start, so an existing window is focused first.
/// </summary>
internal static class MediaAppLauncher
{
    private static readonly Guid AppUserModelIdFormat =
        new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    private const uint AppUserModelIdProperty = 5;
    private const ushort VtLpwstr = 31;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>Opens or focuses the app for the given AppUserModelId; returns false if that failed.</summary>
    public static bool TryOpen(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return false;
        }

        string aumid = appUserModelId.Trim();
        IntPtr existing = FindWindow(aumid);

        if (existing != IntPtr.Zero)
        {
            if (BringToForeground(existing))
            {
                Log.Info($"Focused the existing window of media source app '{aumid}'.");
                return true;
            }

            Log.Warn($"Found the window of '{aumid}' but could not bring it to the foreground.");
        }

        try
        {
            var manager = (IApplicationActivationManager)new ApplicationActivationManager();
            int result = manager.ActivateApplication(aumid, null, ActivateOptions.None, out _);
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            Log.Info($"Activated media source app '{aumid}'.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Activating '{aumid}' directly failed: {ex.GetType().Name}: {ex.Message}");
        }

        // The shell can only help when the app is not running yet. Explorer
        // silently ignores unknown AUMIDs, so require a new matching window.
        if (existing == IntPtr.Zero)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{aumid}")
                {
                    UseShellExecute = true
                });

                if (WaitForWindow(aumid, TimeSpan.FromSeconds(2)))
                {
                    Log.Info($"Opened media source app '{aumid}' through the shell.");
                    return true;
                }

                Log.Warn($"Shell activation of '{aumid}' did not open a matching window.");
            }
            catch (Exception ex)
            {
                Log.Error($"Opening media source app '{aumid}' failed.", ex);
            }
        }

        return false;
    }

    private static bool WaitForWindow(string aumid, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (FindWindow(aumid) != IntPtr.Zero)
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private static IntPtr FindWindow(string aumid)
    {
        IntPtr match = IntPtr.Zero;

        EnumWindows(
            (window, _) =>
            {
                if (!IsWindowVisible(window))
                {
                    return true;
                }

                if (Matches(window, aumid))
                {
                    match = window;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);

        return match;
    }

    private static bool Matches(IntPtr window, string aumid)
    {
        string? id = GetWindowAppUserModelId(window) ?? GetProcessAppUserModelId(window);
        if (string.Equals(id, aumid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Classic Win32 players such as Chrome use their executable name as the
        // media session AUMID and expose no window or process AUMID at all.
        GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            string processName = Process.GetProcessById((int)processId).ProcessName;
            return string.Equals(processName, aumid, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? GetWindowAppUserModelId(IntPtr window)
    {
        IPropertyStore? store = null;
        try
        {
            SHGetPropertyStoreForWindow(window, typeof(IPropertyStore).GUID, out store);
            var key = new PropertyKey
            {
                FormatId = AppUserModelIdFormat,
                PropertyId = AppUserModelIdProperty
            };

            if (store.GetValue(ref key, out PropVariant value) < 0)
            {
                return null;
            }

            try
            {
                return value.VariantType == VtLpwstr && value.PointerValue != IntPtr.Zero
                    ? Marshal.PtrToStringUni(value.PointerValue)
                    : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        catch
        {
            // Not every window exposes a property store.
            return null;
        }
        finally
        {
            if (store is not null)
            {
                Marshal.ReleaseComObject(store);
            }
        }
    }

    private static string? GetProcessAppUserModelId(IntPtr window)
    {
        GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0)
        {
            return null;
        }

        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            uint length = 0;
            if (GetApplicationUserModelId(process, ref length, null) < 0 || length == 0)
            {
                return null;
            }

            var buffer = new StringBuilder((int)length);
            return GetApplicationUserModelId(process, ref length, buffer) >= 0
                ? buffer.ToString()
                : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static bool BringToForeground(IntPtr window)
    {
        if (IsIconic(window))
        {
            ShowWindow(window, SwRestore);
        }

        if (SetForegroundWindow(window))
        {
            return true;
        }

        // A widget action arrives without the foreground rights of a real click.
        // A synthetic Alt keystroke releases the foreground lock for this call.
        keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
        keybd_event(VkMenu, 0, KeyeventfKeyup, UIntPtr.Zero);
        if (SetForegroundWindow(window))
        {
            return true;
        }

        // The foreground lock can still reject the request; attach to the
        // foreground thread for the duration of the call as a fallback.
        IntPtr foreground = GetForegroundWindow();
        uint foregroundThread = foreground == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foreground, out _);
        uint currentThread = GetCurrentThreadId();

        bool attached = foregroundThread != 0 &&
            foregroundThread != currentThread &&
            AttachThreadInput(foregroundThread, currentThread, true);

        try
        {
            if (SetForegroundWindow(window))
            {
                return true;
            }
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(foregroundThread, currentThread, false);
            }
        }

        // A widget action runs without the input foreground rights that a direct
        // click grants. SwitchToThisWindow still raises the existing window.
        SwitchToThisWindow(window, true);
        return GetForegroundWindow() == window;
    }

    private const int SwRestore = 9;
    private const byte VkMenu = 0x12;
    private const uint KeyeventfKeyup = 0x0002;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [ComImport]
    [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            ActivateOptions options,
            out uint processId);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    [ClassInterface(ClassInterfaceType.None)]
    private class ApplicationActivationManager
    {
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort VariantType;

        [FieldOffset(8)]
        public IntPtr PointerValue;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    private enum ActivateOptions
    {
        None = 0
    }

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHGetPropertyStoreForWindow(
        IntPtr window,
        [MarshalAs(UnmanagedType.LPStruct)] Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachInput);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr window, bool altTab);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(
        IntPtr process,
        ref uint applicationUserModelIdLength,
        StringBuilder? applicationUserModelId);
}
