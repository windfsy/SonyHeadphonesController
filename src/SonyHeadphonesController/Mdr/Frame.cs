using System.IO;

namespace SonyCtrl.Mdr;

/// <summary>
/// MDR v2 frame codec.
///
/// Frame layout (verified against Sound Connect v13.3.0 APK qm0/b.java + cp0/b.java):
/// <code>
/// 0x3E | ESC( DataType:1 | Seq:1 | Len:BE32 | Payload | Sum8:1 ) | 0x3C
/// </code>
/// - Start marker 0x3E ('&gt;'), end marker 0x3C ('&lt;').
/// - Payload bytes equal to 0x3C/0x3D/0x3E are escaped as 0x3D followed by (b | 0x10),
///   i.e. 0x3C-&gt;3D 2C, 0x3D-&gt;3D 2D, 0x3E-&gt;3D 2E.
/// - Sum8 checksum covers everything from DataType up to (not including) the checksum byte,
///   computed over the unescaped bytes.
/// - Seq is a 1-bit sequence number (0 or 1) toggled per ACK round-trip.
/// </summary>
internal static class Frame
{
    public const byte StartMarker = 0x3E; // '>'
    public const byte EndMarker = 0x3C;   // '<'
    public const byte EscapeSentry = 0x3D; // '='

    // MDR DataType byte codes (DataType.java in Sound Connect).
    public const byte DataTypeAck = 0x01;
    public const byte DataTypeDataMdr = 0x0C;   // table1 commands
    public const byte DataTypeDataMdrNo2 = 0x0E; // table2 commands

    /// <summary>
    /// Ceiling for one payload. MDR control traffic is tens of bytes (the largest reply we have
    /// captured — the device-info table — stays well under 1 KB). The ceiling exists so a
    /// malformed or hostile length field can neither overflow the bounds check below nor force a
    /// large allocation.
    /// </summary>
    public const int MaxPayloadBytes = 4096;

    /// <summary>
    /// Worst-case wire size of one frame once escaped (every payload byte may expand to two),
    /// used by the reader loop to cap its accumulation buffer.
    /// </summary>
    public const int MaxEscapedFrameBytes = 2 * (6 + MaxPayloadBytes + 1);

    /// <summary>
    /// Build a complete frame ready to write to the RFCOMM socket.
    /// The payload already contains its command bytes (e.g. 0x00 0x00 for INIT).
    /// </summary>
    public static byte[] Encode(byte dataType, byte seq, byte[] payload)
    {
        // Build the unescaped inner buffer: type | seq | len(BE32) | payload
        byte[] inner = new byte[2 + 4 + payload.Length];
        inner[0] = dataType;
        inner[1] = seq;
        int len = payload.Length;
        inner[2] = (byte)((len >> 24) & 0xFF);
        inner[3] = (byte)((len >> 16) & 0xFF);
        inner[4] = (byte)((len >> 8) & 0xFF);
        inner[5] = (byte)(len & 0xFF);
        Buffer.BlockCopy(payload, 0, inner, 6, payload.Length);

        // Append sum8 checksum over the inner bytes (excluding the checksum itself).
        byte checksum = Sum8(inner, 0, inner.Length);
        byte[] innerWithChecksum = new byte[inner.Length + 1];
        Buffer.BlockCopy(inner, 0, innerWithChecksum, 0, inner.Length);
        innerWithChecksum[^1] = checksum;

        // Escape special bytes inside the inner+checksum block.
        byte[] escaped = Escape(innerWithChecksum);

        // Wrap with start/end markers.
        byte[] frame = new byte[2 + escaped.Length];
        frame[0] = StartMarker;
        Buffer.BlockCopy(escaped, 0, frame, 1, escaped.Length);
        frame[^1] = EndMarker;
        return frame;
    }

    /// <summary>
    /// Encode an ACK frame for the given received sequence number.
    /// ACK seq = 1 - receivedSeq (matches the device's behaviour: ACK carries the toggled bit).
    /// Pre-computed ACK frames per Sound Connect qm0/b.java:
    ///   seq 0: 3E 01 00 00 00 00 00 01 3C
    ///   seq 1: 3E 01 01 00 00 00 00 02 3C
    /// </summary>
    public static byte[] EncodeAck(byte receivedSeq)
    {
        byte ackSeq = (byte)(1 - receivedSeq);
        byte[] payload = Array.Empty<byte>();
        return Encode(DataTypeAck, ackSeq, payload);
    }

    /// <summary>
    /// Decode an escaped inner buffer (between start and end markers, markers stripped)
    /// into a parsed message.
    /// </summary>
    public static Message Decode(byte[] escapedInner)
    {
        byte[] unescaped = Unescape(escapedInner);
        if (unescaped.Length < 7)
            throw new FormatException($"Frame too short: {unescaped.Length} bytes (min 7)");

        // Read the length as a long: with int arithmetic a high bit set in unescaped[2] turns the
        // value negative, which silently defeats the bounds check below and indexes backwards.
        long declaredLen =
            ((long)unescaped[2] << 24) |
            ((long)unescaped[3] << 16) |
            ((long)unescaped[4] << 8) |
            unescaped[5];
        if (declaredLen > MaxPayloadBytes)
            throw new FormatException(
                $"Declared payload length {declaredLen} exceeds the {MaxPayloadBytes}-byte ceiling");
        if (unescaped.Length < 6 + declaredLen + 1)
            throw new FormatException($"Declared payload length {declaredLen} exceeds available bytes");

        // Verify checksum over bytes [0 .. 6+declaredLen-1]; checksum sits at index 6+declaredLen.
        int checksumIndex = 6 + (int)declaredLen;
        byte expected = Sum8(unescaped, 0, checksumIndex);
        if (unescaped[checksumIndex] != expected)
            throw new FormatException(
                $"Checksum mismatch: expected 0x{expected:X2}, got 0x{unescaped[checksumIndex]:X2}");

        byte dataType = unescaped[0];
        byte seq = unescaped[1];
        byte[] payload = new byte[(int)declaredLen];
        Buffer.BlockCopy(unescaped, 6, payload, 0, (int)declaredLen);
        return new Message(dataType, seq, payload);
    }

    private static byte[] Escape(byte[] src)
    {
        using var ms = new MemoryStream(src.Length + 8);
        foreach (byte b in src)
        {
            if (b is EndMarker or EscapeSentry or StartMarker)
            {
                ms.WriteByte(EscapeSentry);
                ms.WriteByte((byte)(b | 0x10));
            }
            else
            {
                ms.WriteByte(b);
            }
        }
        return ms.ToArray();
    }

    private static byte[] Unescape(byte[] src)
    {
        using var ms = new MemoryStream(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            byte b = src[i];
            if (b == EscapeSentry)
            {
                if (i + 1 >= src.Length)
                    throw new FormatException("Trailing escape sentry with no following byte");
                ms.WriteByte((byte)(src[++i] | 0x10));
            }
            else
            {
                ms.WriteByte(b);
            }
        }
        return ms.ToArray();
    }

    private static byte Sum8(byte[] buf, int offset, int length)
    {
        int sum = 0;
        for (int i = offset; i < offset + length; i++)
            sum += buf[i];
        return (byte)(sum & 0xFF);
    }
}

internal sealed record Message(byte DataType, byte Seq, byte[] Payload)
{
    public bool IsAck => DataType == Frame.DataTypeAck;
    public bool IsDataMdr => DataType == Frame.DataTypeDataMdr;
    public bool IsDataMdrNo2 => DataType == Frame.DataTypeDataMdrNo2;
}
