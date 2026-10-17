using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace SonyCtrl;

/// <summary>
/// Listens for Bluetooth device arrival/removal via WM_DEVICECHANGE broadcasts
/// (RegisterDeviceNotification + DBT_DEVTYP_DEVICEINTERFACE) and raises events
/// carrying the remote Bluetooth MAC parsed from the device interface path.
///
/// The BTHENUM instance path format is e.g.
///   BTHENUM\{GUID}_VID&0002054C_PID&0E63\8&36B0D96&0&AABBCCDDEEFF_C00000000
/// The 12 hex chars before "_C00000000" in the final segment are the remote MAC.
/// </summary>
internal sealed class DeviceWatcher : IDisposable
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_DEVICEINTERFACE = 0x0005;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x0000;

    // Bluetooth device class GUID (DEVPKEY for Bluetooth radios/devices).
    private static readonly Guid BluetoothClassGuid = new("E0CBF06C-CD8B-4647-BB8A-263B43F0F974");

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_HDR
    {
        public int dbch_size;
        public int dbch_devicetype;
        public int dbch_reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        // Variable-length path; marshal the first char and read the rest from the pointer.
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
        public string dbcc_name;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterDeviceNotificationW(IntPtr hRecipient, ref DEV_BROADCAST_DEVICEINTERFACE notificationFilter, int flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    private readonly Window _host;
    private HwndSource? _source;
    private IntPtr _notifHandle = IntPtr.Zero;
    private readonly HwndSourceHook _hook;

    /// <summary>Raised when the target MAC's device arrives (BT connected).</summary>
    public event Action<ulong>? DeviceArrived;

    /// <summary>Raised when the target MAC's device is removed (BT disconnected).</summary>
    public event Action<ulong>? DeviceRemoved;

    public DeviceWatcher(Window host)
    {
        _host = host;
        _hook = WndProc;
    }

    public void Start()
    {
        // The host window's HWND is force-created via WindowInteropHelper.EnsureHandle()
        // before this call (never Shown). PresentationSource.FromVisual returns null for
        // such a window, but HwndSource.FromHwnd works on the raw handle.
        var helper = new WindowInteropHelper(_host);
        _source = HwndSource.FromHwnd(helper.Handle)
            ?? throw new InvalidOperationException("Host window has no HWND; call EnsureHandle first.");
        _source.AddHook(_hook);

        var filter = new DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
            dbcc_reserved = 0,
            dbcc_classguid = BluetoothClassGuid,
            dbcc_name = "",
        };

        _notifHandle = RegisterDeviceNotificationW(_source.Handle, ref filter, DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_DEVICECHANGE)
            return IntPtr.Zero;

        int evt = wParam.ToInt32();
        if (evt is not (DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE))
            return IntPtr.Zero;

        var hdr = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
        if (hdr.dbch_devicetype != DBT_DEVTYP_DEVICEINTERFACE)
            return IntPtr.Zero;

        var iface = Marshal.PtrToStructure<DEV_BROADCAST_DEVICEINTERFACE>(lParam);
        string path = iface.dbcc_name;

        ulong? mac = ParseMacFromInterfacePath(path);
        if (mac is null)
            return IntPtr.Zero;

        if (evt == DBT_DEVICEARRIVAL)
            DeviceArrived?.Invoke(mac.Value);
        else
            DeviceRemoved?.Invoke(mac.Value);

        return IntPtr.Zero;
    }

    /// <summary>
    /// Extract the remote Bluetooth MAC from a BTHENUM device interface path.
    /// Example: "...\8&36B0D96&0&AABBCCDDEEFF_C00000000" -> AABBCCDDEEFF -> 0xAABBCCDDEEFF.
    /// Local-side instances (no 12-hex segment) return null.
    /// </summary>
    internal static ulong? ParseMacFromInterfacePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        int amp = path.LastIndexOf('&');
        if (amp < 0 || amp + 1 >= path.Length) return null;

        string tail = path[(amp + 1)..];
        int underscore = tail.IndexOf('_');
        string macPart = underscore >= 0 ? tail[..underscore] : tail;

        if (macPart.Length != 12) return null;
        if (!macPart.All(char.IsAsciiHexDigit)) return null;

        return Convert.ToUInt64(macPart, 16);
    }

    public void Dispose()
    {
        _source?.RemoveHook(_hook);
        if (_notifHandle != IntPtr.Zero)
        {
            UnregisterDeviceNotification(_notifHandle);
            _notifHandle = IntPtr.Zero;
        }
    }
}
