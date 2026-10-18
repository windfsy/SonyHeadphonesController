# AGENTS.md — Sony Headphones Controller (WF-1000XM5)

Windows 托盘常驻 WPF 应用（.NET 10，`net10.0-windows`），通过逆向的 Sony MDR v2 蓝牙协议（明文 RFCOMM）控制 WF-1000XM5 的 ANC 与电量读取。协议实现依据是对 Sound Connect (Android) v13.3.0 APK 的逆向分析。

## 构建与验证

```bash
dotnet build SonyHeadphonesController.sln        # 要求零警告（见 CONTRIBUTING.md）
dotnet publish src/SonyHeadphonesController -c Release -o publish
```

- 没有测试项目。功能验证依赖真机（已配对的 WF-1000XM5），改协议/UI 行为后需真机确认。
- **构建前先关掉正在运行的应用**：托盘 exe 会锁定 `bin/.../SonyHeadphonesController.exe`，导致 MSB3026/3027 复制失败（XAML/C# 编译其实已通过，别误判为代码错误）。用 `tasklist | grep Sony` 查 PID 后 `taskkill //PID <pid> //F`。
- 应用无控制台；运行时诊断日志在 `%TEMP%\SonyHeadphonesController.diag.log`，排障先看它。
- 用户配置持久化在 `%APPDATA%\SonyHeadphonesController\config.json`（目标设备名 + MAC）。

## 架构分层（依赖只允许自上而下）

```
UI (WPF)  →  App (连接状态机/同步策略)  →  Mdr (协议帧/命令)  →  Transport (AF_BTH RFCOMM socket)
```

- `src/SonyHeadphonesController/UI/`：托盘（H.NotifyIcon.Wpf）、控制面板、设备选择窗、`MainViewModel`
- `src/SonyHeadphonesController/App/`：`ConnectionManager` 状态机（退避重连 1s→5s→30s、30s 兜底轮询、2min 兜底刷新、命令防护：重试一次后标记 Unresponsive，绝不盲发）
- `src/SonyHeadphonesController/Mdr/`：帧编解码（3E/3C + 转义 + sum8）、ACK/序列号、INIT 握手
- `src/SonyHeadphonesController/Transport/`：BthSocket + 配对设备枚举

关键约束：

- **命名空间是 `SonyCtrl`**，与目录名/程序集名 `SonyHeadphonesController` 不一致，别"顺手统一"。
- **线程边界**：`MdrConnection` 的读线程（"MDR-Reader"）上触发事件；由 App 层通过 Dispatcher 编组回 UI 线程。协议层不得直接碰 WPF 对象。
- 发送命令是同步等待响应（读线程回填字段 + spin-wait），改这里注意别在 UI 线程上引入死锁或长阻塞。

## 动手前必读的文档

- 改 `Mdr/` 或任何协议字节前：**docs/PROTOCOL_NOTES.md**（帧格式、命令码、真机抓包样本、APK 证据索引）
- 改连接/同步/UI 行为前：**docs/SPEC.md**（功能规格 §4、UI 规格 §5、决策记录 §10；代码注释里的 "SPEC §x.y" 均指向它）

## 约定与已知坑

- 提交信息：Conventional Commits 中文风格（`fix(UI): ...`、`docs: ...`），每个提交自洽可构建；任务完成后顺手 commit（用户全局要求）。
- UI 术语用「通透」，不用「环境声」（2026-10 已全库统一，对齐 Sound Connect 官方叫法）。
- WPF 自定义 ControlTemplate 里背景/前景必须走 `TemplateBinding`，否则外部 Style/DataTrigger 的 Setter 不生效（曾导致 ANC 选中高亮 bug：模板硬编码 `#EDEDED`，DataTrigger 的蓝色传不进去）。
- 本机 Git Bash 下 `grep` 传中文模式可能因编码匹配不到，改用 Python `io.open(..., encoding='utf-8')` 扫描更可靠。
- 仅经典蓝牙 RFCOMM；BLE GATT 是预留扩展。音量控制明确不在范围（见 README「已知限制」）。
