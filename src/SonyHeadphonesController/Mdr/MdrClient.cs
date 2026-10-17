using System.Linq;
using SonyCtrl.Transport;

namespace SonyCtrl.Mdr;

/// <summary>
/// High-level MDR v2 client over an RFCOMM BthSocket.
/// Encapsulates the connection handshake (INIT) and exposes typed operations
/// for battery and ANC. Notification events surface state pushed by the device.
/// </summary>
public sealed class MdrClient : IDisposable
{
    private readonly BthSocket _socket = new();
    private MdrConnection? _conn;

    public event Action<BatteryStatus>? OnBatteryNotification;
    public event Action<AncState>? OnAncNotification;

    /// <summary>Raised (from the reader thread) when the control-channel link is lost.</summary>
    public event Action? LinkLost;

    public bool IsConnected => _conn?.IsConnected ?? false;

    /// <summary>
    /// Open RFCOMM control channel to the device and complete the v2 protocol handshake.
    /// Throws on failure. After success, send battery/ANC commands freely.
    /// </summary>
    public void Connect(ulong bluetoothAddress)
    {
        _socket.Connect(bluetoothAddress);
        _conn = new MdrConnection(_socket);
        _conn.OnNotification += HandleNotification;
        _conn.LinkLost += () => LinkLost?.Invoke();
        _conn.Start();

        // INIT handshake: CONNECT_GET_PROTOCOL_INFO = 00 00
        // Expected RET: CONNECT_RET_PROTOCOL_INFO = 01 00 <4-byte version> <2 EnableDisable>
        // Total RET payload is 8 bytes (verified in in0/k.java: bArr.length == 8).
        byte[] ret = _conn.SendCommand(
            new byte[] { CommandId.CONNECT_GET_PROTOCOL_INFO, 0x00 },
            CommandId.CONNECT_RET_PROTOCOL_INFO,
            retSubType: 0x00);

        if (ret.Length < 2)
            throw new InvalidOperationException($"INIT reply too short: {ret.Length} bytes");
        // ret[0]=0x01 (CONNECT_RET_PROTOCOL_INFO), ret[1]=0x00 (FIXED_VALUE).
        // Bytes [2..5] = protocol version (BE32), [6],[7] = EnableDisable flags.
        // We don't gate on version for MVP; presence of the 8-byte RET confirms v2 device.
    }

    /// <summary>
    /// Query left/right/charging-case battery in one call. Issues two GET_STATUS commands
    /// (one for L/R, one for cradle) and merges the result.
    /// </summary>
    public BatteryStatus GetBattery()
    {
        EnsureConnected();
        var status = new BatteryStatus();

        // Left/right: payload = 22 01; RET = 23 01 <L> <L_chg> <R> <R_chg>
        byte[] lr = _conn!.SendCommand(
            new byte[] { CommandId.POWER_GET_STATUS, PowerInquiredType.LEFT_RIGHT_BATTERY },
            CommandId.POWER_RET_STATUS,
            retSubType: PowerInquiredType.LEFT_RIGHT_BATTERY);

        if (lr.Length >= 6)
        {
            // lr[0]=0x23, lr[1]=0x01, lr[2]=L%, lr[3]=L charging, lr[4]=R%, lr[5]=R charging
            status.LeftPercent = lr[2];
            status.LeftCharging = lr[3];
            status.RightPercent = lr[4];
            status.RightCharging = lr[5];
        }

        // Charging case: payload = 22 02; RET = 23 02 <case> <case_chg>
        try
        {
            byte[] cradle = _conn.SendCommand(
                new byte[] { CommandId.POWER_GET_STATUS, PowerInquiredType.CRADLE_BATTERY },
                CommandId.POWER_RET_STATUS,
                retSubType: PowerInquiredType.CRADLE_BATTERY);

            if (cradle.Length >= 4)
            {
                status.CasePercent = cradle[2];
                status.CaseCharging = cradle[3];
            }
        }
        catch (TimeoutException)
        {
            // TWS without a powered case nearby may not respond; treat as unknown.
            status.CasePercent = -1;
        }

        return status;
    }

    /// <summary>
    /// Query current ANC/ambient sound state. Issues NCASM_GET_PARAM with InquiredType=0x17
    /// and parses the 8-byte RET_PARAM: 67 17 01 <on/off> <NC:0/ASM:1> <voicePassthrough> <level>.
    /// Layout matches mos9527/SonyHeadphonesClient serializeNcAndAsmSettingV2 (verified against XM5).
    /// </summary>
    public AncState GetAncMode()
    {
        EnsureConnected();
        byte[] ret = _conn!.SendCommand(
            new byte[] { CommandId.NCASM_GET_PARAM, NcAsmInquiredType.Used },
            CommandId.NCASM_RET_PARAM,
            retSubType: NcAsmInquiredType.Used,
            timeoutMs: 3000);

        return ParseAncState(ret);
    }

    private static AncState ParseAncState(byte[] ret)
    {
        // Real layout on WF-1000XM5 firmware 6.x (verified by hex dump):
        //   67 17 01 <on/off> <NC:0 / ASM:1> <reserved=0> <level>
        // The full payload returned/forwarded by MdrConnection.SendCommand is exactly these
        // 7 bytes (the leading 0x67 command byte is part of the payload, not stripped).
        // Layout: [0]=cmd [1]=inquired [2]=change [3]=on/off [4]=nc/asm [5]=reserved [6]=level
        if (ret.Length < 7)
            throw new InvalidOperationException($"ANC RET too short: {ret.Length} bytes (hex: {ToHex(ret)})");

        byte onOff = ret[3];
        byte ncAsmSelector = ret[4]; // 0=NC, 1=ASM
        byte voicePassthrough = ret[5];
        byte level = ret[6];

        var mode = (onOff, ncAsmSelector) switch
        {
            (0x00, _) => AncMode.Off,
            (0x01, 0x01) => AncMode.AmbientSound,
            (0x01, 0x00) => AncMode.NoiseCancelling,
            _ => throw new InvalidOperationException(
                $"Unknown ANC state: onOff={onOff} ncAsm={ncAsmSelector} (hex: {ToHex(ret)})"),
        };

        return new AncState
        {
            Mode = mode,
            AmbientLevel = level,
            AmbientMode = voicePassthrough == 0x01 ? AmbientModeKind.Voice : AmbientModeKind.Normal,
        };
    }

    private static string ToHex(byte[] b) => string.Join(' ', b.Select(x => $"{x:X2}"));

    /// <summary>
    /// Set ANC mode. Real layout on WF-1000XM5 firmware 6.x (verified by hex dump):
    ///   68 17 01 <on/off> <NC:0 / ASM:1> <reserved=0> <level>
    /// Total 8 bytes. The byte at index [5] is reserved/voice-passthrough (always 0 for our use);
    /// the level lives at index [6]. SET does not return a typed RET; ACK only.
    /// The device pushes NTFY_PARAM (0x69) with the new state, surfaced via OnAncNotification.
    /// </summary>
    public bool SetAncMode(AncMode mode, byte ambientLevel = 0)
    {
        EnsureConnected();
        if (ambientLevel > 20) ambientLevel = 20;

        byte onOff = mode == AncMode.Off ? NcAsmOnOffValue.Off : NcAsmOnOffValue.On;
        byte ncAsmField = mode == AncMode.AmbientSound ? NcAsmSettingTypeV2.AmbientSound
                                                       : NcAsmSettingTypeV2.NoiseCancelling;

        var payload = new byte[]
        {
            CommandId.NCASM_SET_PARAM,                          // 0x68 [0]
            NcAsmInquiredType.Used,                             // 0x17 [1]
            ValueChangeStatus.Changed,                          // 0x01 [2]
            onOff,                                              //      [3]
            ncAsmField,                                         //      [4] 0=NC 1=ASM
            0x00,                                               //      [5] reserved/voice-passthrough
            ambientLevel,                                       //      [6] 0-20
        };

        return _conn!.SendCommandAckOnly(payload, timeoutMs: 2000);
    }

    private void HandleNotification(Message msg)
    {
        if (msg.Payload.Length == 0) return;
        byte cmd = msg.Payload[0];

        if (cmd == CommandId.POWER_NTFY_STATUS && msg.Payload.Length >= 4)
        {
            // 25 <type> <level> <charging>
            byte type = msg.Payload[1];
            var b = new BatteryStatus();
            switch (type)
            {
                case PowerInquiredType.LEFT_RIGHT_BATTERY when msg.Payload.Length >= 6:
                    b.LeftPercent = msg.Payload[2];
                    b.LeftCharging = msg.Payload[3];
                    b.RightPercent = msg.Payload[4];
                    b.RightCharging = msg.Payload[5];
                    break;
                case PowerInquiredType.CRADLE_BATTERY when msg.Payload.Length >= 4:
                    b.CasePercent = msg.Payload[2];
                    b.CaseCharging = msg.Payload[3];
                    break;
            }
            OnBatteryNotification?.Invoke(b);
        }
        else if (cmd == CommandId.NCASM_NTFY_PARAM && msg.Payload.Length >= 7)
        {
            try
            {
                var state = ParseAncState(msg.Payload);
                OnAncNotification?.Invoke(state);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MDR] ANC NTFY parse error: {ex.Message}");
            }
        }
    }

    private void EnsureConnected()
    {
        if (_conn is null || !_conn.IsConnected)
            throw new InvalidOperationException("Not connected to device.");
    }

    public void Dispose()
    {
        _conn?.Dispose();
        _socket.Dispose();
    }
}

public enum AncMode { Off, NoiseCancelling, AmbientSound }

public enum AmbientModeKind { Normal, Voice }

public sealed class AncState
{
    public AncMode Mode { get; set; }
    public byte AmbientLevel { get; set; }
    public AmbientModeKind AmbientMode { get; set; }

    public override string ToString() => Mode switch
    {
        AncMode.Off => "关闭",
        AncMode.NoiseCancelling => "降噪",
        AncMode.AmbientSound => $"环境声 等级{AmbientLevel} ({AmbientMode})",
        _ => "未知",
    };
}

public sealed class BatteryStatus
{
    public int LeftPercent = -1;
    public byte LeftCharging;
    public int RightPercent = -1;
    public byte RightCharging;
    public int CasePercent = -1;
    public byte CaseCharging;

    public override string ToString()
    {
        string L(int p, byte c) => p < 0 ? "未知" : $"{p}% ({BatteryChargingStatus.Label(c)})";
        return $"左耳 {L(LeftPercent, LeftCharging)}  右耳 {L(RightPercent, RightCharging)}  充电盒 {L(CasePercent, CaseCharging)}";
    }
}
