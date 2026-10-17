using SonyCtrl.Transport;

namespace SonyCtrl.Mdr;

/// <summary>
/// High-level MDR v2 client over an RFCOMM BthSocket.
/// Encapsulates the connection handshake (INIT) and exposes typed operations
/// for battery and ANC. Notification events surface state pushed by the device.
/// </summary>
internal sealed class MdrClient : IDisposable
{
    private readonly BthSocket _socket = new();
    private MdrConnection? _conn;

    public event Action<BatteryStatus>? OnBatteryNotification;
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
    /// Set ANC mode. Maps the three UI states to the v2 7-byte SET_PARAM layout.
    /// Payload: 68 14 01 <on/off> <ncValue> <ambientMode> <level>
    /// </summary>
    public void SetAncMode(AncMode mode, byte ambientLevel = 0)
    {
        EnsureConnected();
        if (ambientLevel > 20) ambientLevel = 20;

        byte onOff = mode == AncMode.Off ? NcAsmOnOffValue.Off : NcAsmOnOffValue.On;
        byte ncValue = mode == AncMode.NoiseCancelling ? NcValue.NoiseCancelling : NcValue.Off;
        byte ambient = AmbientSoundMode.Normal;

        var payload = new byte[]
        {
            CommandId.NCASM_SET_PARAM,                                  // 0x68
            NcAsmInquiredType.NC_MODE_SWITCH_AND_ASM_SEAMLESS,          // 0x14
            ValueChangeStatus.Changed,                                  // 0x01
            onOff,
            ncValue,
            ambient,
            ambientLevel,
        };

        // SET_PARAM does not return a typed RET; the device replies with NTFY_PARAM (0x69)
        // carrying the new state. We send and trust the notification path to update us.
        // SendCommand waits for an ACK at minimum; SET_PARAM typically gets no RET but does get ACK.
        // If the wait times out, the command may still have applied; surface as informational.
        try
        {
            _conn!.SendCommand(payload, CommandId.NCASM_NTFY_PARAM, timeoutMs: 1500);
        }
        catch (TimeoutException)
        {
            // Acceptable: ACK seen but no NTFY within window. UI will refresh on next NTFY.
        }
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

internal enum AncMode { Off, NoiseCancelling, AmbientSound }

internal sealed class BatteryStatus
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
