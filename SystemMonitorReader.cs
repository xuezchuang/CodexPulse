using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace CodexPulse;

public sealed record SystemSnapshot(
    double UploadBytesPerSecond,
    double DownloadBytesPerSecond,
    double CpuPercent,
    double MemoryPercent,
    double? CpuTemperatureCelsius);

public sealed class SystemMonitorReader : IDisposable
{
    private const int AsusTemperatureDataType = 3;
    private const int AsusRecordSize = 0x88;
    private const int AsusSensorNameOffset = 0x24;
    private const int AsusBufferSize = 64 * 1024;

    private readonly IntPtr _asusHardwareMonitorLibrary;
    private readonly IntPtr _asusHardwareMonitorBuffer;
    private readonly AsusHardwareMonitorGetData? _getAsusHardwareMonitorData;
    private long? _previousNetworkTimestamp;
    private ulong _previousBytesSent;
    private ulong _previousBytesReceived;
    private bool _hasCpuBaseline;
    private ulong _previousIdleTime;
    private ulong _previousKernelTime;
    private ulong _previousUserTime;
    private bool _disposed;

    public SystemMonitorReader()
    {
        var libraryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "ASUS",
            "Aac_AIOFan",
            "aaHMLib_x64.dll");
        if (!NativeLibrary.TryLoad(libraryPath, out var library))
        {
            return;
        }

        try
        {
            var export = NativeLibrary.GetExport(library, "HM_GetData2");
            _getAsusHardwareMonitorData =
                Marshal.GetDelegateForFunctionPointer<AsusHardwareMonitorGetData>(export);
            _asusHardwareMonitorBuffer = Marshal.AllocHGlobal(AsusBufferSize);
            _asusHardwareMonitorLibrary = library;
        }
        catch
        {
            NativeLibrary.Free(library);
        }
    }

    public SystemSnapshot Sample()
    {
        var (bytesSent, bytesReceived) = ReadNetworkTotals();
        var currentTimestamp = Stopwatch.GetTimestamp();

        var uploadBytesPerSecond = 0d;
        var downloadBytesPerSecond = 0d;
        if (_previousNetworkTimestamp is not null)
        {
            var elapsedSeconds =
                (currentTimestamp - _previousNetworkTimestamp.Value) / (double)Stopwatch.Frequency;
            if (elapsedSeconds > 0)
            {
                uploadBytesPerSecond = CalculateRate(bytesSent, _previousBytesSent, elapsedSeconds);
                downloadBytesPerSecond = CalculateRate(
                    bytesReceived,
                    _previousBytesReceived,
                    elapsedSeconds);
            }
        }

        _previousNetworkTimestamp = currentTimestamp;
        _previousBytesSent = bytesSent;
        _previousBytesReceived = bytesReceived;

        return new SystemSnapshot(
            uploadBytesPerSecond,
            downloadBytesPerSecond,
            ReadCpuPercent(),
            ReadMemoryPercent(),
            ReadCpuTemperatureCelsius());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_asusHardwareMonitorBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_asusHardwareMonitorBuffer);
        }
        if (_asusHardwareMonitorLibrary != IntPtr.Zero)
        {
            NativeLibrary.Free(_asusHardwareMonitorLibrary);
        }
    }

    private double? ReadCpuTemperatureCelsius()
    {
        try
        {
            if (_getAsusHardwareMonitorData is null ||
                _asusHardwareMonitorBuffer == IntPtr.Zero)
            {
                return null;
            }

            var count = _getAsusHardwareMonitorData(
                AsusTemperatureDataType,
                _asusHardwareMonitorBuffer);
            if (count <= 0 || count > AsusBufferSize / AsusRecordSize)
            {
                return null;
            }

            for (var index = 0; index < count; index++)
            {
                var record = IntPtr.Add(
                    _asusHardwareMonitorBuffer,
                    index * AsusRecordSize);
                var sensorName = Marshal.PtrToStringUni(
                    IntPtr.Add(record, AsusSensorNameOffset));
                if (!string.Equals(sensorName, "CPU Package", StringComparison.Ordinal))
                {
                    continue;
                }

                var temperature = Marshal.ReadInt32(record, sizeof(int)) / 10d;
                return temperature is >= 0 and <= 125 ? temperature : null;
            }
        }
        catch
        {
            // Armoury Crate may be updating or restarting its hardware-monitor service.
        }

        return null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AsusHardwareMonitorGetData(int type, IntPtr buffer);

    private static double CalculateRate(ulong current, ulong previous, double elapsedSeconds)
    {
        if (current < previous)
        {
            return 0;
        }

        return (current - previous) / elapsedSeconds;
    }

    private static (ulong Sent, ulong Received) ReadNetworkTotals()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        var preferred = interfaces.Where(IsPreferredNetworkInterface).ToArray();
        var selected = preferred.Length > 0
            ? preferred
            : interfaces.Where(IsFallbackNetworkInterface).ToArray();

        ulong sent = 0;
        ulong received = 0;
        foreach (var networkInterface in selected)
        {
            try
            {
                var statistics = networkInterface.GetIPv4Statistics();
                sent += (ulong)Math.Max(0, statistics.BytesSent);
                received += (ulong)Math.Max(0, statistics.BytesReceived);
            }
            catch (NetworkInformationException)
            {
                // An adapter may disappear while the list is being sampled.
            }
        }

        return (sent, received);
    }

    private static bool IsPreferredNetworkInterface(NetworkInterface networkInterface)
    {
        if (!IsFallbackNetworkInterface(networkInterface))
        {
            return false;
        }

        if (networkInterface.NetworkInterfaceType is not (
            NetworkInterfaceType.Ethernet or
            NetworkInterfaceType.Ethernet3Megabit or
            NetworkInterfaceType.FastEthernetFx or
            NetworkInterfaceType.FastEthernetT or
            NetworkInterfaceType.GigabitEthernet or
            NetworkInterfaceType.Wireless80211))
        {
            return false;
        }

        var identity = $"{networkInterface.Name} {networkInterface.Description}";
        return !ContainsAny(
            identity,
            "virtual",
            "vethernet",
            "hyper-v",
            "loopback",
            "vpn",
            "tap",
            "tun",
            "tailscale",
            "zerotier");
    }

    private static bool IsFallbackNetworkInterface(NetworkInterface networkInterface)
    {
        return networkInterface.OperationalStatus == OperationalStatus.Up &&
               networkInterface.NetworkInterfaceType is not (
                   NetworkInterfaceType.Loopback or
                   NetworkInterfaceType.Tunnel or
                   NetworkInterfaceType.Unknown);
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate =>
            value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private double ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0;
        }

        var idleTime = ToUInt64(idle);
        var kernelTime = ToUInt64(kernel);
        var userTime = ToUInt64(user);
        if (!_hasCpuBaseline)
        {
            _hasCpuBaseline = true;
            _previousIdleTime = idleTime;
            _previousKernelTime = kernelTime;
            _previousUserTime = userTime;
            return 0;
        }

        var idleDelta = idleTime - _previousIdleTime;
        var kernelDelta = kernelTime - _previousKernelTime;
        var userDelta = userTime - _previousUserTime;
        var totalDelta = kernelDelta + userDelta;

        _previousIdleTime = idleTime;
        _previousKernelTime = kernelTime;
        _previousUserTime = userTime;

        if (totalDelta == 0 || idleDelta > totalDelta)
        {
            return 0;
        }

        return Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
    }

    private static double ReadMemoryPercent()
    {
        var status = new MemoryStatus
        {
            Length = (uint)Marshal.SizeOf<MemoryStatus>()
        };
        return GlobalMemoryStatusEx(ref status)
            ? Math.Clamp(status.MemoryLoad, 0, 100)
            : 0;
    }

    private static ulong ToUInt64(NativeFileTime value)
    {
        return ((ulong)value.HighDateTime << 32) | value.LowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out NativeFileTime idleTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
