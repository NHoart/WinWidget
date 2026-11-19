using System.Runtime.InteropServices;

namespace CustomWidgets.WidgetProvider.Desktop;

/// <summary>
/// Reads and writes the Windows master volume through the Core Audio endpoint.
/// The desktop host uses this for the volume slider instead of emulating the
/// keyboard volume keys, so the bar reflects and controls the actual level.
/// </summary>
internal sealed class VolumeController : IDisposable
{
    private const int EDataFlowRender = 0;
    private const int ERoleMultimedia = 1;
    private const int ClsCtxAll = 0x17;
    private const int StgmRead = 0;
    private const ushort VtLpwstr = 31;
    private const ushort VtBstr = 8;

    private static readonly PropertyKey DeviceFriendlyNameKey = new(
        new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        14);

    private IAudioEndpointVolume? _endpoint;
    private string? _deviceId;
    private string? _deviceName;
    private DateTime _nextInitializationAttemptUtc = DateTime.MinValue;
    private bool _disposed;

    public string? DeviceName => _deviceName;

    public bool TryGetState(out int percent, out bool muted)
    {
        percent = 0;
        muted = false;

        if (!EnsureEndpoint(refreshDevice: true))
        {
            return false;
        }

        try
        {
            int result = _endpoint!.GetMasterVolumeLevelScalar(out float level);
            Marshal.ThrowExceptionForHR(result);

            result = _endpoint.GetMute(out muted);
            Marshal.ThrowExceptionForHR(result);

            percent = (int)Math.Round(Math.Clamp(level, 0f, 1f) * 100);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Master volume could not be read: {ex.GetType().Name}: {ex.Message}");
            ReleaseEndpoint();
            return false;
        }
    }

    public bool SetVolume(int percent)
    {
        if (!EnsureEndpoint(refreshDevice: false))
        {
            return false;
        }

        try
        {
            Guid eventContext = Guid.Empty;
            int result = _endpoint!.SetMasterVolumeLevelScalar(
                Math.Clamp(percent, 0, 100) / 100f,
                ref eventContext);
            Marshal.ThrowExceptionForHR(result);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Master volume could not be changed: {ex.GetType().Name}: {ex.Message}");
            ReleaseEndpoint();
            return false;
        }
    }

    public bool ToggleMute(out bool muted)
    {
        muted = false;

        if (!EnsureEndpoint(refreshDevice: true))
        {
            return false;
        }

        try
        {
            int result = _endpoint!.GetMute(out bool currentMute);
            Marshal.ThrowExceptionForHR(result);

            Guid eventContext = Guid.Empty;
            result = _endpoint.SetMute(!currentMute, ref eventContext);
            Marshal.ThrowExceptionForHR(result);

            muted = !currentMute;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Master mute could not be changed: {ex.GetType().Name}: {ex.Message}");
            ReleaseEndpoint();
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        ReleaseEndpoint();
    }

    private bool EnsureEndpoint(bool refreshDevice)
    {
        if (_disposed)
        {
            return false;
        }

        if (_endpoint is not null && !refreshDevice)
        {
            return true;
        }

        if (_endpoint is null && DateTime.UtcNow < _nextInitializationAttemptUtc)
        {
            return false;
        }

        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;

        try
        {
            var enumeratorObject = new MMDeviceEnumeratorComObject();
            enumerator = (IMMDeviceEnumerator)enumeratorObject;

            int result = enumerator.GetDefaultAudioEndpoint(
                EDataFlowRender,
                ERoleMultimedia,
                out device);
            Marshal.ThrowExceptionForHR(result);

            result = device.GetId(out string deviceId);
            Marshal.ThrowExceptionForHR(result);

            if (_endpoint is not null &&
                string.Equals(deviceId, _deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string? deviceName = ReadDeviceName(device);
            Guid interfaceId = typeof(IAudioEndpointVolume).GUID;
            result = device.Activate(
                ref interfaceId,
                ClsCtxAll,
                IntPtr.Zero,
                out object endpointObject);
            Marshal.ThrowExceptionForHR(result);

            bool changed = _endpoint is not null;
            ReleaseEndpoint();
            _endpoint = (IAudioEndpointVolume)endpointObject;
            _deviceId = deviceId;
            _deviceName = deviceName;
            _nextInitializationAttemptUtc = DateTime.MinValue;

            string displayName = string.IsNullOrWhiteSpace(deviceName)
                ? "Unbekanntes Gerät"
                : deviceName;
            Log.Info(changed
                ? $"Audio endpoint switched to: {displayName} ({deviceId})"
                : $"Audio endpoint bound: {displayName} ({deviceId})");

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Audio endpoint for the volume slider is unavailable: {ex.GetType().Name}: {ex.Message}");
            ReleaseEndpoint();
            _nextInitializationAttemptUtc = DateTime.UtcNow.AddSeconds(5);
            return false;
        }
        finally
        {
            if (device is not null)
            {
                Marshal.ReleaseComObject(device);
            }

            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
    }

    private void ReleaseEndpoint()
    {
        if (_endpoint is not null)
        {
            Marshal.ReleaseComObject(_endpoint);
            _endpoint = null;
        }

        _deviceId = null;
        _deviceName = null;
    }

    private static string? ReadDeviceName(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            int result = device.OpenPropertyStore(StgmRead, out store);
            Marshal.ThrowExceptionForHR(result);

            PropertyKey key = DeviceFriendlyNameKey;
            result = store.GetValue(ref key, out PropVariant value);
            Marshal.ThrowExceptionForHR(result);

            try
            {
                if (value.vt == VtLpwstr && value.value != IntPtr.Zero)
                {
                    return Marshal.PtrToStringUni(value.value);
                }

                if (value.vt == VtBstr && value.value != IntPtr.Zero)
                {
                    return Marshal.PtrToStringBSTR(value.value);
                }

                return null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Audio device name could not be read: {ex.GetType().Name}: {ex.Message}");
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

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey
    {
        public PropertyKey(Guid formatId, int propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }

        public Guid FormatId { get; }
        public int PropertyId { get; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort vt;

        [FieldOffset(8)]
        public IntPtr value;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            int classContext,
            IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);

        [PreserveSig]
        int OpenPropertyStore(int access, out IPropertyStore properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int GetChannelCount(out uint count);

        [PreserveSig]
        int SetMasterVolumeLevel(float level, ref Guid eventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float level);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(uint channel, float level, ref Guid eventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(uint channel, out float level);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channel, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
