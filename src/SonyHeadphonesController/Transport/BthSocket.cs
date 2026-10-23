using System.Runtime.InteropServices;

namespace SonyCtrl.Transport;

/// <summary>
/// Classic Bluetooth RFCOMM client socket over Windows AF_BTH.
/// Equivalent of Android's BluetoothSocket.createRfcommSocketToServiceRecord(uuid):
/// Windows resolves the RFCOMM channel from the SDP service UUID passed in SOCKADDR_BTH.
/// </summary>
internal sealed class BthSocket : IDisposable
{
    // --- ws2_32 / bthprops constants ---
    private const int AF_BTH = 32;
    private const int BTHPROTO_RFCOMM = 3;
    private const int SOCK_STREAM = 1;
    private const int SOL_RFCOMM = 3;
    private const int SO_BTH_AUTHENTICATE = unchecked((int)0x80000001);
    private const int SO_BTH_ENCRYPT = unchecked((int)0x80000002);
    private const int INVALID_SOCKET = -1;
    private const int SOCKET_ERROR = -1;

    // Sony MDR v2 SDP service UUID (XM5 generation and newer).
    // Verified from Sound Connect APK lm0/i.java and cross-checked with
    // mos9527/SonyHeadphonesClient libmdr/include/mdr-c/Base.h.
    public static readonly Guid ServiceUuidV2 = Guid.Parse("956c7b26-d49a-4ba8-b03f-b17d393cb6e2");

    // Sony MDR v1 SDP service UUID (XM4 and older). Fallback only.
    public static readonly Guid ServiceUuidV1 = Guid.Parse("96cc203e-5068-46ad-b32d-e316f5e069ba");

    private int _socket = INVALID_SOCKET;

    [StructLayout(LayoutKind.Sequential)]
    private struct WSAData
    {
        public ushort wVersion;
        public ushort wHighVersion;
        public ushort iMaxSockets;
        public ushort iMaxUdpDg;
        public IntPtr lpVendorInfo;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
        public string szDescription;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)]
        public string szSystemStatus;
    }

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int WSAStartup(ushort wVersionRequested, ref WSAData lpWSAData);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int WSACleanup();

    private static int _wsaRefCount;
    private static readonly object _wsaLock = new();

    /// <summary>
    /// Ensure Winsock is initialized. Idempotent; reference-counted so multiple
    /// BthSocket instances in the same process share one WSAStartup/WSACleanup pair.
    /// </summary>
    public static void EnsureWinsock()
    {
        lock (_wsaLock)
        {
            if (_wsaRefCount > 0) { _wsaRefCount++; return; }
            var data = new WSAData();
            // Request Winsock 2.2.
            int rc = WSAStartup(0x0202, ref data);
            if (rc != 0)
                throw new System.Net.Sockets.SocketException(rc);
            _wsaRefCount = 1;
        }
    }

    public static void ReleaseWinsock()
    {
        lock (_wsaLock)
        {
            if (_wsaRefCount == 0) return;
            if (--_wsaRefCount == 0)
                WSACleanup();
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SOCKADDR_BTH
    {
        public ushort addressFamily;     // AF_BTH
        public ulong btAddr;              // 6-byte Bluetooth MAC, little-endian within the ulong
        public Guid serviceClassId;       // SDP service UUID the stack resolves to a DLCI
        public int port;                  // 0 = resolve via SDP using serviceClassId
    }

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int socket(int af, int type, int protocol);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int closesocket(int s);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int connect(int s, ref SOCKADDR_BTH name, int namelen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int setsockopt(int s, int level, int optname, ref int optval, int optlen);

    // send() takes a raw address rather than a managed array: retrying a partial write must
    // resume at the byte offset the stack already accepted (see Send).
    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int send(int s, IntPtr buf, int len, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int recv(int s, byte[] buf, int len, int flags);

    /// <summary>
    /// Connect to the given Bluetooth MAC, resolving the RFCOMM DLCI via SDP using
    /// the MDR v2 service UUID. Throws on failure.
    /// </summary>
    public void Connect(ulong bluetoothAddress)
    {
        EnsureWinsock();
        int s = socket(AF_BTH, SOCK_STREAM, BTHPROTO_RFCOMM);
        if (s == INVALID_SOCKET)
            throw new System.Net.Sockets.SocketException(Marshal.GetLastWin32Error());

        // mos9527/SonyHeadphonesClient sets SO_BTH_AUTHENTICATE+ENCRYPT on Windows before connect.
        // The MediaTek BT stack on the dev machine rejects these options with WSAENOPROTOOPT but
        // does not require them; we set them best-effort and ignore the failure. The link is
        // already authenticated/encrypted by the pairing layer for a paired device.
        int enable = 1;
        try { setsockopt(s, SOL_RFCOMM, SO_BTH_AUTHENTICATE, ref enable, sizeof(int)); }
        catch (System.Net.Sockets.SocketException) { /* WSAENOPROTOOPT - ignored */ }
        try { setsockopt(s, SOL_RFCOMM, SO_BTH_ENCRYPT, ref enable, sizeof(int)); }
        catch (System.Net.Sockets.SocketException) { /* WSAENOPROTOOPT - ignored */ }

        var addr = new SOCKADDR_BTH
        {
            addressFamily = AF_BTH,
            btAddr = bluetoothAddress,
            serviceClassId = ServiceUuidV2,
            port = 0, // 0 => Windows performs SDP lookup using serviceClassId
        };

        // Blocking connect with a connect timeout enforced via SO_SNDTIMEO.
        // Windows' blocking connect respects SO_SNDTIMEO for Bluetooth sockets.
        const int SOL_SOCKET = unchecked((int)0xFFFF);
        const int SO_SNDTIMEO = 0x1005;
        int timeoutMs = 8000;
        setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, ref timeoutMs, sizeof(int));

        int rc = connect(s, ref addr, Marshal.SizeOf<SOCKADDR_BTH>());
        if (rc == SOCKET_ERROR)
        {
            int err = Marshal.GetLastWin32Error();
            closesocket(s);
            throw new System.Net.Sockets.SocketException(err);
        }

        _socket = s;
    }

    /// <summary>
    /// Send the whole buffer, resuming from the accepted offset on partial writes.
    /// Advancing the address matters: re-sending from the array head would duplicate bytes on the
    /// wire and desynchronise the frame.
    /// </summary>
    public void Send(byte[] buffer)
    {
        if (_socket == INVALID_SOCKET) throw new InvalidOperationException("Not connected.");

        GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            IntPtr start = pinned.AddrOfPinnedObject();
            int total = 0;
            while (total < buffer.Length)
            {
                int sent = send(_socket, IntPtr.Add(start, total), buffer.Length - total, 0);
                if (sent == SOCKET_ERROR)
                    throw new System.Net.Sockets.SocketException(Marshal.GetLastWin32Error());
                if (sent == 0) break;
                total += sent;
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>Receive up to buffer.Length bytes. Returns 0 on peer close.</summary>
    public int Receive(byte[] buffer)
    {
        if (_socket == INVALID_SOCKET) throw new InvalidOperationException("Not connected.");
        int n = recv(_socket, buffer, buffer.Length, 0);
        if (n == SOCKET_ERROR)
            throw new System.Net.Sockets.SocketException(Marshal.GetLastWin32Error());
        return n;
    }

    public bool Connected => _socket != INVALID_SOCKET;

    public void Dispose()
    {
        if (_socket != INVALID_SOCKET)
        {
            closesocket(_socket);
            _socket = INVALID_SOCKET;
        }
        ReleaseWinsock();
    }
}
