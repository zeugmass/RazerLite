using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RazerLite;

internal static class HidWin
{
    public const int ReportSize = 90;
    public const int FeatureSize = 91;

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    private static readonly Guid HidGuid = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, ref int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    public enum LinkMode
    {
        Dongle,
        Wired,
        Wireless,
        Unknown,
    }

    public sealed record DeviceInfo(int Pid, string Name, LinkMode Mode)
    {
        public string ModeText => Mode switch
        {
            LinkMode.Dongle => "Dongle",
            LinkMode.Wired => "Kablolu",
            LinkMode.Wireless => "Kablosuz",
            _ => "Bilinmeyen",
        };

        public string Title => Mode == LinkMode.Dongle ? Name : $"{Name} ({ModeText})";
    }

    // Ayni 90 baytlik Razer protokolunu (tx 0x1F) konusan bilinen cihazlar.
    // Kaynak: OpenRazer razermouse_driver.h PID listesi.
    public static readonly Dictionary<int, DeviceInfo> KnownDevices = new()
    {
        [0x00B3] = new(0x00B3, "HyperPolling Wireless Dongle", LinkMode.Dongle),
        [0x00B6] = new(0x00B6, "DeathAdder V3 Pro", LinkMode.Wired),
        [0x00B7] = new(0x00B7, "DeathAdder V3 Pro", LinkMode.Wireless),
        [0x00C2] = new(0x00C2, "DeathAdder V3 Pro", LinkMode.Wired),
        [0x00C3] = new(0x00C3, "DeathAdder V3 Pro", LinkMode.Wireless),
        [0x00BE] = new(0x00BE, "DeathAdder V4 Pro", LinkMode.Wired),
        [0x00BF] = new(0x00BF, "DeathAdder V4 Pro", LinkMode.Wireless),
        [0x00C0] = new(0x00C0, "Viper V3 Pro", LinkMode.Wired),
        [0x00C1] = new(0x00C1, "Viper V3 Pro", LinkMode.Wireless),
        [0x00B2] = new(0x00B2, "DeathAdder V3", LinkMode.Wired),
        [0x00C4] = new(0x00C4, "DeathAdder V3 HyperSpeed", LinkMode.Wired),
        [0x00C5] = new(0x00C5, "DeathAdder V3 HyperSpeed", LinkMode.Wireless),
        [0x00B8] = new(0x00B8, "Viper V3 HyperSpeed", LinkMode.Wireless),
    };

    public static DeviceInfo Describe(int pid) =>
        KnownDevices.TryGetValue(pid, out var info)
            ? info
            : new DeviceInfo(pid, $"Razer PID {pid:X4}", LinkMode.Unknown);

    public sealed class Device : IDisposable
    {
        public SafeFileHandle Handle { get; private set; }
        public string Path { get; private set; }
        public DeviceInfo Info { get; private set; }
        public int Pid => Info.Pid;

        /// <summary>
        /// null: henuz bilinmiyor; true: 0x00/0x40-0xC0 genisletilmis polling (125-8000 Hz);
        /// false: klasik 0x00/0x05-0x85 (125/500/1000 Hz). Ilk GetPolling'de ogrenilir.
        /// </summary>
        public bool? ExtendedPolling { get; set; }

        public Device(SafeFileHandle handle, string path)
        {
            Handle = handle;
            Path = path;
            Info = Describe(PidFromPath(path));
        }

        public void Dispose()
        {
            if (!Handle.IsInvalid && !Handle.IsClosed)
            {
                Handle.Dispose();
            }
        }

        public void Adopt(SafeFileHandle handle, string path)
        {
            if (!Handle.IsInvalid && !Handle.IsClosed)
            {
                Handle.Dispose();
            }

            Handle = handle;
            Path = path;
            var info = Describe(PidFromPath(path));
            if (info.Pid != Info.Pid)
            {
                ExtendedPolling = null; // farkli cihaz: yetenegi yeniden ogren
            }

            Info = info;
        }

        public SafeFileHandle Detach()
        {
            var handle = Handle;
            Handle = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
            return handle;
        }
    }

    public static int PidFromPath(string path)
    {
        var p = path.ToLowerInvariant();
        int i = p.IndexOf("pid_", StringComparison.Ordinal);
        if (i >= 0 && i + 8 <= p.Length && int.TryParse(p.AsSpan(i + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out var pid))
        {
            return pid;
        }

        return 0;
    }

    private static bool IsControlPath(string path)
    {
        var p = path.ToLowerInvariant();
        return p.Contains("vid_1532")
            && p.Contains("pid_")
            && p.Contains("mi_00")
            && !p.Contains("col");
    }

    /// <summary>
    /// Aday siralamasi: tercih edilen PID, sonra bilinen kablolu fare (kablo takiliyken
    /// dongle da takili olabilir; aktif olan kablodur), sonra dongle/kablosuz, en son bilinmeyen.
    /// </summary>
    private static int Rank(string path, int? preferPid)
    {
        var info = Describe(PidFromPath(path));
        if (preferPid is int pref && info.Pid == pref)
        {
            return 0;
        }

        return info.Mode switch
        {
            LinkMode.Wired => 1,
            LinkMode.Dongle => 2,
            LinkMode.Wireless => 3,
            _ => 9,
        };
    }

    public static List<string> ListControlPaths(int? preferPid = null)
    {
        return ListControlPathsRaw()
            .OrderBy(p => Rank(p, preferPid))
            .ToList();
    }

    private static List<string> ListControlPathsRaw()
    {
        var guid = HidGuid;
        var info = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (info == IntPtr.Zero || info == new IntPtr(-1))
        {
            throw new InvalidOperationException($"SetupDiGetClassDevs failed ({Marshal.GetLastWin32Error()})");
        }

        var paths = new List<string>();
        try
        {
            for (uint i = 0; i < 512; i++)
            {
                var iface = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(info, IntPtr.Zero, ref guid, i, ref iface))
                {
                    break;
                }

                int needed = 0;
                SetupDiGetDeviceInterfaceDetail(info, ref iface, IntPtr.Zero, 0, ref needed, IntPtr.Zero);
                if (needed < 8)
                {
                    continue;
                }

                var detail = Marshal.AllocHGlobal(needed);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(info, ref iface, detail, needed, ref needed, IntPtr.Zero))
                    {
                        continue;
                    }

                    var path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    if (!string.IsNullOrEmpty(path) && IsControlPath(path))
                    {
                        paths.Add(path);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(info);
        }

        return paths;
    }

    public static Device Open(int? preferPid = null)
    {
        var paths = ListControlPaths(preferPid);
        var errors = new List<string>();

        foreach (var path in paths)
        {
            var handle = CreateFile(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle is { IsInvalid: false })
            {
                return new Device(handle, path);
            }

            errors.Add($"{path} ({Marshal.GetLastWin32Error()})");
        }

        throw new InvalidOperationException(
            paths.Count == 0
                ? "Razer HID control collection (MI_00) not found."
                : "Could not open Razer HID control collection.\n" + string.Join("\n", errors));
    }

    public static void SetFeature(Device device, byte[] report)
    {
        if (report.Length != ReportSize)
        {
            throw new ArgumentException("Razer report must be 90 bytes.");
        }

        var feature = new byte[FeatureSize];
        Buffer.BlockCopy(report, 0, feature, 1, ReportSize);
        if (!HidD_SetFeature(device.Handle, feature, FeatureSize))
        {
            throw new InvalidOperationException($"HidD_SetFeature failed ({Marshal.GetLastWin32Error()})");
        }
    }

    public static byte[] GetFeature(Device device)
    {
        var feature = new byte[FeatureSize];
        if (!HidD_GetFeature(device.Handle, feature, FeatureSize))
        {
            throw new InvalidOperationException($"HidD_GetFeature failed ({Marshal.GetLastWin32Error()})");
        }

        var report = new byte[ReportSize];
        Buffer.BlockCopy(feature, 1, report, 0, ReportSize);
        return report;
    }

    public static async Task ReconnectAsync(Device device, int timeoutMs = 8000)
    {
        // Ayni cihaza (PID) geri baglanmayi tercih et.
        int preferPid = device.Pid;
        device.Dispose();

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var next = Open(preferPid);
                var path = next.Path;
                var handle = next.Detach();
                device.Adopt(handle, path);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(150);
            }
        }

        throw new InvalidOperationException(
            "Device did not reappear after polling-rate change: " + (last?.Message ?? "timeout"));
    }
}
