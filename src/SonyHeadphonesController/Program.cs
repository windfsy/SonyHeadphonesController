using SonyCtrl.Mdr;
using SonyCtrl.Transport;

// L2 milestone: ANC read + three-state switch + ambient level control.
// Run with: dotnet run
// Make sure the earbuds are out of the case and paired to this PC.

Console.WriteLine("枚举已配对的蓝牙设备...");
var devices = BluetoothDeviceEnumerator.EnumeratePairedDevices();
if (devices.Count == 0)
{
    Console.Error.WriteLine("没有找到已配对的蓝牙设备。请先在 Windows 设置中与耳机配对。");
    return 1;
}

var sony = devices.Where(d => d.Name.Contains("WF-", StringComparison.OrdinalIgnoreCase)
                           || d.Name.Contains("WH-", StringComparison.OrdinalIgnoreCase)
                           || d.Name.Contains("Sony", StringComparison.OrdinalIgnoreCase)).ToList();
var pick = sony.Count > 0 ? sony : devices;

Console.WriteLine("\n已配对设备：");
for (int i = 0; i < pick.Count; i++)
    Console.WriteLine($"  [{i}] {pick[i].Name}  ({pick[i].Address:X12}){(pick[i].Connected ? "  已连接" : "")}");

Console.Write($"\n选择目标耳机的序号 (默认 0): ");
string? line = Console.ReadLine();
int idx = int.TryParse(line, out int v) ? v : 0;
ulong btAddr = pick[idx].Address;
Console.WriteLine($"目标蓝牙地址: {btAddr:X12}\n");

using var client = new MdrClient();
client.OnBatteryNotification += b => Console.WriteLine($"[NTFY 电量] {b}");
client.OnAncNotification += s => Console.WriteLine($"[NTFY ANC]  {s}");

Console.WriteLine("正在建立 RFCOMM 控制通道...");
try { client.Connect(btAddr); }
catch (Exception ex)
{
    Console.Error.WriteLine($"连接失败: {ex.Message}");
    Console.Error.WriteLine("提示：请确认耳机已从充电盒取出并与电脑配对。");
    return 2;
}
Console.WriteLine("✓ 控制通道建立，INIT 握手完成（v2 设备已确认）\n");

// Initial full state pull.
Console.WriteLine("=== 当前状态 ===");
var battery = client.GetBattery();
Console.WriteLine($"电量: {battery}");
var anc = client.GetAncMode();
Console.WriteLine($"ANC:  {anc}\n");

// Interactive loop.
Console.WriteLine("命令: [1]读ANC [2]切降噪 [3]切环境声 [4]切关闭 [5]调环境声等级 [6]读电量 [q]退出");
while (true)
{
    Console.Write("> ");
    string? input = Console.ReadLine();
    if (string.IsNullOrEmpty(input)) continue;
    char c = input[0];

    try
    {
        switch (c)
        {
            case '1':
                var s = client.GetAncMode();
                Console.WriteLine($"ANC: {s}");
                break;
            case '2':
                Console.WriteLine("切换到降噪...");
                bool ok2 = client.SetAncMode(AncMode.NoiseCancelling);
                Console.WriteLine(ok2 ? "  ✓ 已发送（ACK 已收到）" : "  ✗ ACK 未收到");
                Thread.Sleep(500);
                Console.WriteLine($"  当前: {client.GetAncMode()}");
                break;
            case '3':
                Console.WriteLine("切换到环境声 (等级10)...");
                bool ok3 = client.SetAncMode(AncMode.AmbientSound, ambientLevel: 10);
                Console.WriteLine(ok3 ? "  ✓ 已发送（ACK 已收到）" : "  ✗ ACK 未收到");
                Thread.Sleep(500);
                Console.WriteLine($"  当前: {client.GetAncMode()}");
                break;
            case '4':
                Console.WriteLine("切换到关闭...");
                bool ok4 = client.SetAncMode(AncMode.Off);
                Console.WriteLine(ok4 ? "  ✓ 已发送（ACK 已收到）" : "  ✗ ACK 未收到");
                Thread.Sleep(500);
                Console.WriteLine($"  当前: {client.GetAncMode()}");
                break;
            case '5':
                Console.Write("环境声等级 (0-20): ");
                string? lvlStr = Console.ReadLine();
                if (!int.TryParse(lvlStr, out int lvl) || lvl < 0 || lvl > 20)
                {
                    Console.WriteLine("  无效输入");
                    break;
                }
                Console.WriteLine($"设置环境声等级 {lvl}...");
                bool ok5 = client.SetAncMode(AncMode.AmbientSound, ambientLevel: (byte)lvl);
                Console.WriteLine(ok5 ? "  ✓ 已发送（ACK 已收到）" : "  ✗ ACK 未收到");
                Thread.Sleep(500);
                Console.WriteLine($"  当前: {client.GetAncMode()}");
                break;
            case '6':
                var b = client.GetBattery();
                Console.WriteLine($"电量: {b}");
                break;
            case 'q' or 'Q':
                Console.WriteLine("退出。");
                return 0;
            default:
                Console.WriteLine("未知命令。");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  命令失败: {ex.Message}");
    }
}
