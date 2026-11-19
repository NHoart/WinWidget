using System.Runtime.InteropServices;

namespace CustomWidgets.WidgetProvider.Services;

internal enum BatteryState
{
    Unknown,
    NoBattery,
    Charging,
    OnAc,
    OnBattery
}

internal sealed record SystemSnapshot(
    double? CpuPercent,
    double? MemoryPercent,
    ulong MemoryUsedBytes,
    ulong MemoryTotalBytes,
    int? BatteryPercent,
    BatteryState Battery,
    int LogicalProcessors,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Samples CPU, RAM and battery only when a widget asks for new content. The CPU
/// figure needs a short delta between two kernel counters; there is no background
/// timer and no polling while the provider is idle.
/// </summary>
internal sealed class SystemMetricsService
{
    private const byte NoSystemBattery = 128;
    private const byte BatteryIsCharging = 8;
    private const byte BatteryPercentUnknown = 255;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<SystemSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            double? cpuPercent = null;
            try
            {
                if (TryReadCpuTimes(out ulong startIdle, out ulong startKernel, out ulong startUser))
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);

                    if (TryReadCpuTimes(out ulong endIdle, out ulong endKernel, out ulong endUser))
                    {
                        ulong idle = endIdle - startIdle;
                        ulong total = (endKernel - startKernel) + (endUser - startUser);
                        if (total > 0)
                        {
                            cpuPercent = Math.Clamp((1.0 - ((double)idle / total)) * 100.0, 0.0, 100.0);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"CPU sampling failed: {ex.GetType().Name}: {ex.Message}");
            }

            ReadMemory(out ulong usedBytes, out ulong totalBytes, out double? memoryPercent);
            ReadBattery(out int? batteryPercent, out BatteryState batteryState);

            return new SystemSnapshot(
                cpuPercent,
                memoryPercent,
                usedBytes,
                totalBytes,
                batteryPercent,
                batteryState,
                Environment.ProcessorCount,
                DateTimeOffset.Now);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool TryReadCpuTimes(out ulong idle, out ulong kernel, out ulong user)
    {
        idle = 0;
        kernel = 0;
        user = 0;

        if (!GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime))
        {
            return false;
        }

        idle = ToUInt64(idleTime);
        kernel = ToUInt64(kernelTime);
        user = ToUInt64(userTime);
        return true;
    }

    private static void ReadMemory(out ulong usedBytes, out ulong totalBytes, out double? percent)
    {
        usedBytes = 0;
        totalBytes = 0;
        percent = null;

        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
        {
            return;
        }

        totalBytes = status.TotalPhys;
        usedBytes = status.TotalPhys > status.AvailPhys ? status.TotalPhys - status.AvailPhys : 0;
        percent = (double)usedBytes / totalBytes * 100.0;
    }

    private static void ReadBattery(out int? percent, out BatteryState state)
    {
        percent = null;
        state = BatteryState.Unknown;

        if (!GetSystemPowerStatus(out SystemPowerStatus status))
        {
            return;
        }

        state = status.BatteryFlag == 255
            ? BatteryState.Unknown
            : (status.BatteryFlag & NoSystemBattery) != 0
                ? BatteryState.NoBattery
                : (status.BatteryFlag & BatteryIsCharging) != 0
                    ? BatteryState.Charging
                    : status.ACLineStatus == 1
                        ? BatteryState.OnAc
                        : status.ACLineStatus == 0
                            ? BatteryState.OnBattery
                            : BatteryState.Unknown;

        if (state != BatteryState.NoBattery && status.BatteryLifePercent != BatteryPercentUnknown)
        {
            percent = Math.Clamp((int)status.BatteryLifePercent, 0, 100);
        }
    }

    private static ulong ToUInt64(FileTime time) => ((ulong)time.High << 32) | time.Low;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
