using System.Runtime.InteropServices;

namespace SonyCtrl.Transport;

/// <summary>
/// Enumerates paired/powered Bluetooth devices known to the Windows stack via
/// BluetoothFindFirstDevice/Next. The classic radio API returns remembered
/// (paired) devices, which is what the user expects for first-run selection.
/// </summary>
internal static class BluetoothDeviceEnumerator
{
    private const int BLUETOOTH_MAX_NAME_SIZE = 248;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_DEVICE_INFO
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        public bool fConnected;
        public bool fRemembered;
        public bool fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = BLUETOOTH_MAX_NAME_SIZE)]
        public string szName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear;
        public ushort wMonth;
        public ushort wDayOfWeek;
        public ushort wDay;
        public ushort wHour;
        public ushort wMinute;
        public ushort wSecond;
        public ushort wMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
    {
        public uint dwSize;
        public bool fReturnAuthenticated;
        public bool fReturnRemembered;
        public bool fReturnUnknown;
        public bool fReturnConnected;
        public bool fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [DllImport("bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "BluetoothFindFirstDevice")]
    private static extern IntPtr BthFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS searchParams, ref BLUETOOTH_DEVICE_INFO deviceInfo);

    [DllImport("bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO deviceInfo);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

    public sealed record Device(string Name, ulong Address, bool Connected, bool Authenticated);

    /// <summary>
    /// Returns all paired (authenticated) devices known to the system.
    /// Walks the BLUETOOTH_DEVICE_SEARCH_PARAMS list with fReturnAuthenticated = true.
    /// </summary>
    public static List<Device> EnumeratePairedDevices()
    {
        var list = new List<Device>();

        var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
            fReturnAuthenticated = true,
            fReturnRemembered = false,
            fReturnUnknown = false,
            fReturnConnected = false,
            fIssueInquiry = false,
            cTimeoutMultiplier = 0,
            hRadio = IntPtr.Zero,
        };

        var info = new BLUETOOTH_DEVICE_INFO
        {
            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(),
        };

        // bthprops.cpl is the canonical home on modern Windows; Irprops.cpl is the legacy fallback.
        IntPtr hFind = BthFindFirstDevice(ref search, ref info);
        if (hFind == IntPtr.Zero)
        {
            // Try the legacy entry point.
            hFind = IrpropsFindFirstDevice(ref search, ref info);
            if (hFind == IntPtr.Zero)
                return list;
        }

        try
        {
            do
            {
                list.Add(new Device(info.szName ?? "(unknown)", info.Address, info.fConnected, info.fAuthenticated));
                info.dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>();
            }
            while (BluetoothFindNextDevice(hFind, ref info));
        }
        finally
        {
            BluetoothFindDeviceClose(hFind);
        }

        return list;
    }

    [DllImport("Irprops.cpl", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "BluetoothFindFirstDevice")]
    private static extern IntPtr IrpropsFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS searchParams, ref BLUETOOTH_DEVICE_INFO deviceInfo);
}
