namespace SonyCtrl.Mdr;

/// <summary>
/// MDR v2 command byte opcodes for table1 (DATA_MDR).
/// Sourced from Sound Connect v13.3.0 APK Command.java enum and cross-checked
/// with mos9527/SonyHeadphonesClient libmdr/include/mdr-c/Base.h.
/// </summary>
internal static class CommandId
{
    // CONNECT family (connection handshake / capability discovery)
    public const byte CONNECT_GET_PROTOCOL_INFO = 0x00;
    public const byte CONNECT_RET_PROTOCOL_INFO = 0x01;
    public const byte CONNECT_GET_CAPABILITY_INFO = 0x02;
    public const byte CONNECT_RET_CAPABILITY_INFO = 0x03;
    public const byte CONNECT_GET_DEVICE_INFO = 0x04;
    public const byte CONNECT_RET_DEVICE_INFO = 0x05;

    // POWER family (battery lives here)
    public const byte POWER_GET_STATUS = 0x22;
    public const byte POWER_RET_STATUS = 0x23;
    public const byte POWER_NTFY_STATUS = 0x25;

    // NCASM family (ANC / ambient sound)
    public const byte NCASM_GET_PARAM = 0x66;
    public const byte NCASM_RET_PARAM = 0x67;
    public const byte NCASM_SET_PARAM = 0x68;
    public const byte NCASM_NTFY_PARAM = 0x69;
}

/// <summary>
/// Sub-type bytes for the POWER_GET_STATUS payload[1].
/// From PowerInquiredType.java in the APK.
/// </summary>
internal static class PowerInquiredType
{
    public const byte BATTERY = 0x00;              // single combined gauge
    public const byte LEFT_RIGHT_BATTERY = 0x01;   // L + R separate
    public const byte CRADLE_BATTERY = 0x02;       // charging case
}

/// <summary>
/// BatteryChargingStatus byte values from BatteryChargingStatus.java.
/// Returned in the 4th/6th byte of POWER_RET_STATUS for L/R battery.
/// </summary>
internal static class BatteryChargingStatus
{
    public const byte NotCharging = 0;
    public const byte Charging = 1;
    public const byte Unknown = 2;
    public const byte Charged = 3;

    public static string Label(byte b) => b switch
    {
        NotCharging => "未充电",
        Charging => "充电中",
        Charged => "已充满",
        _ => "未知",
    };
}

/// <summary>
/// NCASM InquiredType byte values from NcAsmInquiredType.java.
/// Two relevant forms:
///   NC_MODE_SWITCH_AND_ASM_SEAMLESS (0x14) — full 7-byte layout incl. ambient volume level.
///   NC_MODE_SWITCH_AND_ASM_ON_OFF   (0x17) — used by mos9527's SonyHeadphonesClient for XM5,
///                                            verified against real device.
/// We use 0x17 because it's the form tested against WF-1000XM5 by the community.
/// </summary>
internal static class NcAsmInquiredType
{
    public const byte NC_MODE_SWITCH_AND_ASM_SEAMLESS = 0x14;
    public const byte NC_MODE_SWITCH_AND_ASM_ON_OFF = 0x17;
    // The form actually used by SetAncMode/GetAncMode:
    public const byte Used = NC_MODE_SWITCH_AND_ASM_ON_OFF;
}

internal static class NcAsmOnOffValue
{
    public const byte Off = 0x00;
    public const byte On = 0x01;
}

internal static class NcValue
{
    public const byte Off = 0x00;
    public const byte NoiseCancelling = 0x01; // single
}

/// <summary>
/// For the 0x17 InquiredType layout, the "nc/asm selector" field is 0=NC, 1=ASM
/// (opposite of the NcValue enum used for the 0x14 layout). From NC_ASM_SETTING_TYPE_V2
/// in mos9527/SonyHeadphonesClient Constants.h.
/// </summary>
internal static class NcAsmSettingTypeV2
{
    public const byte NoiseCancelling = 0x00;
    public const byte AmbientSound = 0x01;
}

internal static class AmbientSoundMode
{
    public const byte Normal = 0x00;
    public const byte Voice = 0x01;
}

internal static class ValueChangeStatus
{
    public const byte UnderChanging = 0x00;
    public const byte Changed = 0x01;
}
