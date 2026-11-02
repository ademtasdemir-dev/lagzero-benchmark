using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lagzero.Core;

/// <summary>Primary monitor: current mode in physical pixels (EnumDisplaySettings is independent of Windows scaling) + all supported modes.</summary>
public sealed record MonitorInfo(int Width, int Height, int RefreshHz, int NativeWidth, int NativeHeight, List<(int W, int H)> Modes)
{
    public string Resolution => $"{Width}×{Height}";
    public bool Supports(int w, int h) => Modes.Contains((w, h));
    public override string ToString() =>
        $"{Width}×{Height} @ {RefreshHz} Hz" + (NativeWidth * NativeHeight > Width * Height ? $" (höchste unterstützte Auflösung {NativeWidth}×{NativeHeight})" : "");
}

public static class Monitors
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? device, int mode, ref DEVMODE dm);

    private const int ENUM_CURRENT_SETTINGS = -1;

    /// <summary>Primary monitor (device null = primary display). Null if it cannot be read.</summary>
    public static MonitorInfo? Primary()
    {
        try
        {
            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref dm)) return null;
            int w = (int)dm.dmPelsWidth, h = (int)dm.dmPelsHeight, hz = (int)dm.dmDisplayFrequency;
            var modes = new HashSet<(int, int)>();
            for (int i = 0; ; i++)
            {
                var m = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
                if (!EnumDisplaySettingsW(null, i, ref m)) break;
                modes.Add(((int)m.dmPelsWidth, (int)m.dmPelsHeight));
            }
            modes.Add((w, h));
            var native = modes.OrderByDescending(m => (long)m.Item1 * m.Item2).First();
            return new MonitorInfo(w, h, hz, native.Item1, native.Item2, modes.ToList());
        }
        catch { return null; }
    }
}

/// <summary>Interface class of a physical disk: PCIe generation/lanes for NVMe, USB link speed for USB disks.</summary>
public sealed record DiskInterface(string Kind, string ClassName, string Detail, bool DeviceCanBeFaster, string? DeviceNote)
{
    public static readonly DiskInterface Unknown = new("unknown", "unbekannt", "", false, null);
}

public static class DiskInterfaces
{
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Locate_DevNodeW(out uint devInst, string id, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_IDW(uint devInst, char[] buf, int len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[]? buf, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid cls, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid cls, string id, char[] buf, uint len, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inLen, byte[] outBuf, int outLen, out int ret, IntPtr ov);

    [StructLayout(LayoutKind.Sequential)] private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    private static readonly Guid PciProps = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62");
    private static readonly Guid DeviceProps = new("a45c254e-df1c-4efd-8020-67d146a850e0"); // DEVPKEY_Device_Address = 30
    private static Guid HubInterface = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

    private static string Id(uint dev)
    {
        var b = new char[400];
        return CM_Get_Device_IDW(dev, b, b.Length, 0) == 0 ? new string(b).TrimEnd('\0') : "";
    }

    private static uint? PropU32(uint dev, Guid g, uint pid)
    {
        var key = new DEVPROPKEY { fmtid = g, pid = pid };
        uint size = 4; var buf = new byte[4];
        return CM_Get_DevNode_PropertyW(dev, ref key, out _, buf, ref size, 0) == 0 && size >= 4 ? BitConverter.ToUInt32(buf, 0) : null;
    }

    /// <summary>Walks from the disk's PnP device up to its PCIe controller or USB device and reads the real link.</summary>
    public static DiskInterface ForDisk(int diskNumber, int busType, int mediaType)
    {
        try
        {
            string pnp = SystemInfo.Wmi($"SELECT PNPDeviceID FROM Win32_DiskDrive WHERE Index = {diskNumber}")
                .Select(o => SystemInfo.S(o, "PNPDeviceID")).FirstOrDefault() ?? "";
            if (pnp.Length == 0 || CM_Locate_DevNodeW(out uint dev, pnp, 0) != 0) return Fallback(busType, mediaType);

            for (int depth = 0; depth < 8; depth++)
            {
                string id = Id(dev);
                if (busType == 17 && id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
                {
                    uint? gen = PropU32(dev, PciProps, 9), lanes = PropU32(dev, PciProps, 10);
                    uint? maxGen = PropU32(dev, PciProps, 11), maxLanes = PropU32(dev, PciProps, 12);
                    if (gen is > 0 and < 8 && lanes is > 0)
                    {
                        bool slower = (maxGen ?? gen) > gen || (maxLanes ?? lanes) > lanes;
                        return new DiskInterface("nvme", $"NVMe PCIe Gen{gen}", $"PCIe Gen{gen} x{lanes}", slower,
                            slower ? $"Die SSD kann PCIe Gen{maxGen} x{maxLanes}, läuft aber mit Gen{gen} x{lanes}." : null)
                        { };
                    }
                    break;
                }
                if (id.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase))
                {
                    if (CM_Get_Parent(out uint hub, dev, 0) != 0) break;
                    uint? port = PropU32(dev, DeviceProps, 30);
                    var usb = QueryUsbPort(Id(hub), port ?? 0);
                    if (usb != null) return usb;
                    break;
                }
                if (CM_Get_Parent(out uint parent, dev, 0) != 0) break;
                dev = parent;
            }
        }
        catch { }
        return Fallback(busType, mediaType);
    }

    private static DiskInterface Fallback(int busType, int mediaType) => busType switch
    {
        11 when mediaType == 3 => new("hdd", "HDD (SATA)", "SATA", false, null),
        11 => new("sata", "SATA-SSD", "SATA 6 Gbit/s", false, null),
        17 => new("nvme", "NVMe (PCIe-Generation nicht lesbar)", "NVMe", false, null),
        7 => new("usb", "USB (Geschwindigkeit nicht lesbar)", "USB", false, null),
        _ => mediaType == 3 ? new("hdd", "HDD", "", false, null) : DiskInterface.Unknown,
    };

    /// <summary>IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX (+ _V2) on the parent hub for the device's port.</summary>
    private static DiskInterface? QueryUsbPort(string hubId, uint port)
    {
        if (port == 0) return null;
        if (CM_Get_Device_Interface_List_SizeW(out uint len, ref HubInterface, hubId, 0) != 0 || len <= 1) return null;
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref HubInterface, hubId, buf, len, 0) != 0) return null;
        string path = new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (path.Length == 0) return null;
        using var h = CreateFileW(path, 0x40000000 /*GENERIC_WRITE*/, 2 /*FILE_SHARE_WRITE*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;

        var inf = new byte[512];
        BitConverter.GetBytes(port).CopyTo(inf, 0);
        if (!DeviceIoControl(h, 0x220448, inf, inf.Length, inf, inf.Length, out _, IntPtr.Zero)) return null;
        ushort bcdUsb = BitConverter.ToUInt16(inf, 4 + 2);  // USB_DEVICE_DESCRIPTOR.bcdUSB
        byte speed = inf[4 + 18 + 1];                      // after descriptor + CurrentConfigurationValue

        bool superPlus = false, ssCapable = bcdUsb >= 0x0300, ssPlusCapable = false;
        var v2 = new byte[16];
        BitConverter.GetBytes(port).CopyTo(v2, 0);
        BitConverter.GetBytes(16u).CopyTo(v2, 4);
        BitConverter.GetBytes(7u).CopyTo(v2, 8);           // USB 1.1 | 2.0 | 3.0 protocols
        if (DeviceIoControl(h, 0x22045C, v2, 16, v2, 16, out _, IntPtr.Zero))
        {
            uint flags = BitConverter.ToUInt32(v2, 12);
            superPlus = (flags & 4) != 0;
            ssCapable |= (flags & 2) != 0;
            ssPlusCapable = (flags & 8) != 0;
            if ((flags & 1) != 0 && speed < 3) speed = 3;
        }

        string bcd = $"{bcdUsb >> 8}.{(bcdUsb >> 4) & 0xF}" + ((bcdUsb & 0xF) != 0 ? (bcdUsb & 0xF).ToString() : "");
        string cls = speed switch
        {
            >= 3 when superPlus => "usb3_10",
            >= 3 => "usb3_5",
            2 => "usb2",
            _ => "usb1",
        };
        string name = cls switch
        {
            "usb3_10" => "USB 3.x 10 Gbit/s", "usb3_5" => "USB 3.x 5 Gbit/s", "usb2" => "USB 2.0", _ => "USB 1.1",
        };
        bool canBeFaster = (cls is "usb2" or "usb1" && ssCapable) || (cls == "usb3_5" && ssPlusCapable);
        bool viaHub = !hubId.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase);
        var hv = System.Text.RegularExpressions.Regex.Match(hubId, @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        string hubText = viaHub ? $"über einen zwischengeschalteten USB-Hub{(hv.Success ? $" ({hv.Groups[1].Value}:{hv.Groups[2].Value})" : "")}" : "direkt am PC";
        string? note = canBeFaster
            ? $"Das Gerät beherrscht {(ssPlusCapable ? "USB 3.x mit 10 Gbit/s" : "USB 3.x")}, ist aber nur mit {name} verbunden ({hubText})."
            : null;
        string detail = $"{name} · {(viaHub ? "über USB-Hub" : "direkt am PC")}" + (ssCapable ? " · Gerät USB-3-fähig" : $" · Gerät meldet USB {bcd}");
        return new DiskInterface(cls, name, detail, canBeFaster, note);
    }
}
