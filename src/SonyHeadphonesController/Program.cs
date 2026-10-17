using SonyCtrl.Mdr;
using SonyCtrl.Transport;

// L1 milestone: connect to WF-1000XM5 over RFCOMM, run INIT handshake, read battery.
// Run with: dotnet run
// Make sure the earbuds are out of the case and paired to this PC.

string target = args.Length > 0 ? args[0] : "";
ulong btAddr;

if (!string.IsNullOrEmpty(target))
{
    btAddr = ParseBtAddress(target);
}
else
{
    Console.WriteLine("枚举已配对的蓝牙设备...");
    var devices = BluetoothDeviceEnumerator.EnumeratePairedDevices();
    if (devices.Count == 0)
    {
        Console.Error.WriteLine("没有找到已配对的蓝牙设备。请先在 Windows 设置中与耳机配对。");
        return 1;
    }

    // Prefer Sony WF/WH devices by name; otherwise list all and let user pick.
    var sony = devices.Where(d => d.Name.Contains("WF-", StringComparison.OrdinalIgnoreCase)
                               || d.Name.Contains("WH-", StringComparison.OrdinalIgnoreCase)
                               || d.Name.Contains("Sony", StringComparison.OrdinalIgnoreCase)).ToList();

    var pick = sony.Count > 0 ? sony : devices;

    Console.WriteLine("\n已配对设备：");
    for (int i = 0; i < pick.Count; i++)
    {
        var d = pick[i];
        Console.WriteLine($"  [{i}] {d.Name}  ({d.Address:X12}){(d.Connected ? "  已连接" : "")}");
    }

    Console.Write($"\n选择目标耳机的序号 (默认 0): ");
    string? line = Console.ReadLine();
    int idx = int.TryParse(line, out int v) ? v : 0;
    btAddr = pick[idx].Address;
}

Console.WriteLine($"\n目标蓝牙地址: {btAddr:X12}");

using var client = new MdrClient();
client.OnBatteryNotification += b => Console.WriteLine($"[NTFY] 电量更新: {b}");

Console.WriteLine("正在建立 RFCOMM 控制通道...");
try
{
    client.Connect(btAddr);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"连接失败: {ex.Message}");
    Console.Error.WriteLine("提示：请确认耳机已从充电盒取出并与电脑配对。");
    return 2;
}

Console.WriteLine("✓ 控制通道建立，INIT 握手完成（v2 设备已确认）\n");

Console.WriteLine("正在查询电量...");
try
{
    var battery = client.GetBattery();
    Console.WriteLine($"\n=== 电量 ===\n{battery}\n");
    Console.WriteLine("L1 验收通过：连接 + INIT + 电量读取全部成功。");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"电量查询失败: {ex.Message}");
    return 3;
}

static ulong ParseBtAddress(string s)
{
    // Accept "AABBCCDDEEFF" or "AA:BB:CC:DD:EE:FF".
    s = s.Replace(":", "").Replace("-", "").Trim();
    return ulong.Parse(s, System.Globalization.NumberStyles.HexNumber);
}
