using System.Diagnostics;
using System.IO;
using SonyCtrl.Transport;

namespace SonyCtrl.Mdr;

/// <summary>
/// Manages a single MDR v2 session over an RFCOMM BthSocket:
/// - Reads the byte stream, splits out frames between 0x3E/0x3C markers.
/// - Auto-sends ACK for every DATA_MDR / DATA_MDR_NO2 frame received (device retrains until ACK).
/// - Tracks a 1-bit sequence number for outgoing commands and matches ACKs/replies.
///
/// State machine for sendCommandAndRead: send DATA_MDR frame with seq N -> read frames until
///   (a) ACK with seq 1-N arrives (advance expected seq), then
///   (b) the matching RET frame arrives, return its payload.
/// The device may also push NTFY frames interleaved; those are surfaced via OnNotification
/// but do not consume the awaited RET slot.
/// </summary>
internal sealed class MdrConnection : IDisposable
{
    private readonly BthSocket _socket;
    private readonly Thread _readerThread;
    private readonly object _lock = new();
    // Leftover bytes from a recv() that span frame boundaries; the reader loop replays them.
    private byte[] _leftover = Array.Empty<byte>();
    private byte _sendSeq = 0;
    private bool _disposed;

    // Pending RET matching: a single outstanding command waits for one ACK + one RET.
    private byte _pendingAckSeq = 0xFF;     // 0xFF = no pending
    private byte _pendingRetCommandId = 0xFF;
    private byte _pendingRetSubType = 0xFF; // 0xFF = any sub-type
    private Message? _matchedRet;
    private bool _ackSeen;

    public event Action<Message>? OnNotification;

    public MdrConnection(BthSocket socket)
    {
        _socket = socket;
        _readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "MDR-Reader" };
    }

    public bool IsConnected => _socket.Connected;

    public void Start()
    {
        _readerThread.Start();
    }

    /// <summary>
    /// Send a DATA_MDR (table1) command and synchronously wait for its ACK + matching RET.
    /// </summary>
    /// <param name="commandBytes">Full payload beginning with the command byte, e.g. 0x00 0x00 for INIT.</param>
    /// <param name="retCommandId">Expected first byte of the RET frame's payload.</param>
    /// <param name="retSubType">Optional second-byte match; pass null to accept any.</param>
    /// <param name="timeoutMs">Total wait timeout.</param>
    public byte[] SendCommand(byte[] commandBytes, byte retCommandId, byte? retSubType = null, int timeoutMs = 4000)
    {
        lock (_lock)
        {
            if (_pendingAckSeq != 0xFF)
                throw new InvalidOperationException("A command is already in flight.");
            _pendingAckSeq = _sendSeq;
            _pendingRetCommandId = retCommandId;
            _pendingRetSubType = retSubType ?? 0xFF;
            _matchedRet = null;
            _ackSeen = false;
        }

        byte[] frame = Frame.Encode(Frame.DataTypeDataMdr, _sendSeq, commandBytes);
        _socket.Send(frame);
        _sendSeq = (byte)(1 - _sendSeq); // toggle for the next command

        // Spin-wait with timeout. The reader thread delivers results by mutating the fields above.
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            lock (_lock)
            {
                if (_matchedRet is not null)
                {
                    // Clear pending state before returning so the next SendCommand sees a clean slate.
                    byte[] payload = _matchedRet.Payload;
                    _matchedRet = null;
                    _pendingAckSeq = 0xFF;
                    _pendingRetCommandId = 0xFF;
                    _pendingRetSubType = 0xFF;
                    _ackSeen = false;
                    return payload;
                }
            }
            Thread.Sleep(10);
        }

        // Timeout: clear pending state so a subsequent SendCommand isn't blocked.
        lock (_lock)
        {
            _matchedRet = null;
            _pendingAckSeq = 0xFF;
            _pendingRetCommandId = 0xFF;
            _pendingRetSubType = 0xFF;
            _ackSeen = false;
        }
        throw new TimeoutException(
            $"No matching RET 0x{retCommandId:X2} within {timeoutMs} ms");
    }

    /// <summary>
    /// Send a command that only requires an ACK (no RET). Used for SET_PARAM commands whose
    /// effect is confirmed asynchronously via NTFY_PARAM, not by a typed RET reply.
    /// Returns true if ACK was received within timeout, false otherwise.
    /// </summary>
    public bool SendCommandAckOnly(byte[] commandBytes, int timeoutMs = 2000)
    {
        lock (_lock)
        {
            if (_pendingAckSeq != 0xFF)
                throw new InvalidOperationException("A command is already in flight.");
            _pendingAckSeq = _sendSeq;
            _pendingRetCommandId = 0xFF; // 0xFF => match no RET, only the ACK
            _pendingRetSubType = 0xFF;
            _matchedRet = null;
            _ackSeen = false;
        }

        byte[] frame = Frame.Encode(Frame.DataTypeDataMdr, _sendSeq, commandBytes);
        _socket.Send(frame);
        _sendSeq = (byte)(1 - _sendSeq); // toggle for the next command

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            lock (_lock)
            {
                if (_ackSeen)
                {
                    _matchedRet = null;
                    _pendingAckSeq = 0xFF;
                    _pendingRetCommandId = 0xFF;
                    _pendingRetSubType = 0xFF;
                    _ackSeen = false;
                    return true;
                }
            }
            Thread.Sleep(10);
        }

        // Timeout: clear pending state so a subsequent SendCommand isn't blocked.
        lock (_lock)
        {
            _matchedRet = null;
            _pendingAckSeq = 0xFF;
            _pendingRetCommandId = 0xFF;
            _pendingRetSubType = 0xFF;
            _ackSeen = false;
        }
        return false;
    }

    private void ReaderLoop()
    {
        byte[] recvBuf = new byte[2048];
        var frameBuf = new MemoryStream(2048);
        bool inFrame = false;

        try
        {
            while (!_disposed && _socket.Connected)
            {
            int n;
            // Prefer leftover bytes (a previous recv() may have already buffered the next frame start).
            if (_leftover.Length > 0)
            {
                n = Math.Min(_leftover.Length, recvBuf.Length);
                Buffer.BlockCopy(_leftover, 0, recvBuf, 0, n);
                byte[] rest = new byte[_leftover.Length - n];
                Buffer.BlockCopy(_leftover, n, rest, 0, rest.Length);
                _leftover = rest;
            }
            else
            {
                try { n = _socket.Receive(recvBuf); }
                catch { break; }
                if (n <= 0) break;
            }

            int i = 0;
            while (i < n)
            {
                byte b = recvBuf[i++];

                if (b == Frame.StartMarker)
                {
                    if (inFrame)
                    {
                        // Two start markers in a row: resync.
                        frameBuf.SetLength(0);
                    }
                    inFrame = true;
                    continue;
                }

                if (!inFrame)
                    continue;

                if (b == Frame.EndMarker)
                {
                    byte[] escapedInner = frameBuf.ToArray();
                    frameBuf.SetLength(0);
                    inFrame = false;
                    try { HandleFrame(escapedInner); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[MDR] frame parse error: {ex.Message}");
                    }
                    // If the recv buffer had more bytes beyond this end marker, keep them for next iteration.
                    if (i < n)
                    {
                        byte[] tail = new byte[n - i];
                        Buffer.BlockCopy(recvBuf, i, tail, 0, tail.Length);
                        _leftover = tail;
                        i = n; // exit inner loop; outer loop will replay _leftover
                    }
                }
                else
                {
                    frameBuf.WriteByte(b);
                }
            }
            }
        }
        finally
        {
            // Reader loop exit == link loss (peer closed, socket error, or dispose).
            // Raise on the reader thread; the app layer marshals to the UI thread.
            try { LinkLost?.Invoke(); } catch { }
        }
    }

    /// <summary>Raised (from the reader thread) when the socket read loop exits.</summary>
    public event Action? LinkLost;

    private void HandleFrame(byte[] escapedInner)
    {
        Message msg = Frame.Decode(escapedInner);

        if (msg.IsAck)
        {
            lock (_lock)
            {
                if (_pendingAckSeq != 0xFF && msg.Seq == (byte)(1 - _pendingAckSeq))
                    _ackSeen = true;
            }
            return;
        }

        // For every DATA_MDR / DATA_MDR_NO2 frame we receive, send an ACK back.
        if (msg.IsDataMdr || msg.IsDataMdrNo2)
        {
            try { _socket.Send(Frame.EncodeAck(msg.Seq)); }
            catch { /* ignore ACK send failure; device will retransmit */ }

            bool matchedPending = false;
            lock (_lock)
            {
                if (_pendingAckSeq != 0xFF &&
                    msg.Payload.Length > 0 &&
                    msg.Payload[0] == _pendingRetCommandId &&
                    (_pendingRetSubType == 0xFF || (msg.Payload.Length > 1 && msg.Payload[1] == _pendingRetSubType)))
                {
                    _matchedRet = msg;
                    matchedPending = true;
                }
            }

            if (!matchedPending)
            {
                // NTFY (notification) frame: surface to subscribers.
                OnNotification?.Invoke(msg);
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _socket.Dispose(); } catch { }
    }
}
