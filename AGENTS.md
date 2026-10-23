# AGENTS.md — Sony Headphones Controller (WF-1000XM5)

Windows 托盘常驻 WPF 应用（.NET 10，`net10.0-windows`），通过逆向的 Sony MDR v2 蓝牙协议（明文 RFCOMM）控制 WF-1000XM5 的 ANC 与电量读取。协议实现依据是对 Sound Connect (Android) v13.3.0 APK 的逆向分析。

## 构建与验证

```bash
dotnet build SonyHeadphonesController.sln        # 要求零警告（见 CONTRIBUTING.md）
dotnet publish src/SonyHeadphonesController -c Release -o publish
```

- 没有测试项目。功能验证依赖真机，改协议/UI 行为后需真机确认。
- 构建前先关掉正在运行的应用：托盘 exe 会锁定 `bin/.../SonyHeadphonesController.exe`，导致 MSB3026/3027 复制失败（XAML/C# 编译其实已通过，别误判为代码错误）。用 `tasklist | grep Sony` 查 PID 后 `taskkill //PID <pid> //F`。
- 应用无控制台；运行时诊断日志在 `%TEMP%\SonyHeadphonesController.diag.log`，排障先看它。
- 用户配置持久化在 `%APPDATA%\SonyHeadphonesController\config.json`（目标设备名 + MAC）。

## 架构分层

```
UI (WPF)  →  App (连接状态机/同步策略)  →  Mdr (协议帧/命令)  →  Transport (AF_BTH RFCOMM socket)
```

- `src/SonyHeadphonesController/UI/`：托盘（H.NotifyIcon.Wpf）、控制面板、设备选择窗、`MainViewModel`
- `src/SonyHeadphonesController/App/`：`ConnectionManager` 状态机
- `src/SonyHeadphonesController/Mdr/`：帧编解码、ACK/序列号、INIT 握手
- `src/SonyHeadphonesController/Transport/`：BthSocket + 配对设备枚举

关键约束：

- 命名空间是 `SonyCtrl`**，与目录名/程序集名 `SonyHeadphonesController` 不一致，别"顺手统一"。
- 线程边界：`MdrConnection` 的读线程（"MDR-Reader"）上触发事件；由 App 层通过 Dispatcher 编组回 UI 线程。协议层不得直接碰 WPF 对象。
- 发送命令是同步等待响应（读线程回填字段 + spin-wait），改这里注意别在 UI 线程上引入死锁或长阻塞。

## 动手前必读的文档

- 改 `Mdr/` 或任何协议字节前：**docs/PROTOCOL_NOTES.md**（帧格式、命令码、真机抓包样本、APK 证据索引）
- 改连接/同步/UI 行为前：**docs/SPEC.md**（功能规格 §4、UI 规格 §5、决策记录 §10）
- 事实存在差异需及时更新文档

## 约定与已知坑

- 提交信息：Conventional Commits 中文风格（`fix(UI): ...`、`docs: ...`），每个提交自洽可构建；任务完成后顺手 commit（用户全局要求）。
- 仅经典蓝牙 RFCOMM；BLE GATT 是预留扩展。音量控制明确不在范围（见 README「已知限制」）。
- 品牌资产三处同源，替换图标时须一起改：`src/SonyHeadphonesController/Assets/app.ico`（exe 图标 + 窗口图标，多尺寸 16–256）、`assets/icon.png`（README/社交图用 512×512）、`assets/hero.png`（README 开头主图）。exe 图标由 csproj `ApplicationIcon` 指定，窗口图标用 pack URI 引用同一 ico。
- 托盘图标是**运行时绘制的电量徽章**（见 `MainViewModel.TrayIconSource`），刻意不是程序图标——别"顺手统一"，那会让电量在托盘上不可见。
- 发版两处版本号必须同步：`CHANGELOG.md` 顶部新增 `## [x.y.z]` 小节 + csproj `<Version>`；Release workflow 会校验 tag 与 CHANGELOG 一致，不一致直接失败。
