# 安全策略

## 支持的版本

仅最新发布版本接受安全修复。

| 版本 | 支持 |
|---|---|
| 最新 release | ✅ |
| 更早版本 | ❌ |

## 报告漏洞

请**不要**通过公开 Issue 报告安全问题。任选其一：

1. 使用 GitHub 的[私密漏洞报告](https://github.com/windfsy/SonyHeadphonesController/security/advisories/new)（推荐）
2. 邮件联系维护者（见提交记录中的作者邮箱）

请在报告中包含：影响版本、复现步骤、影响范围（例如"可被同网段设备读取电量"），以及你建议的修复方向（如有）。

## 威胁模型

本应用是一个本地托盘程序，不监听网络端口、不上传数据、不写入注册表。已知的信任边界：

- **蓝牙链路**：MDR v2 为明文 RFCOMM，与官方 app 一致；同一物理空间内的攻击者理论上可注入协议帧（Sony 协议本身不提供认证）。缓解措施：仅当 Windows 已持有一条到目标设备的链路时才发起连接，不会主动 page 设备。
- **配置与日志**：`%APPDATA%\SonyHeadphonesController\config.json` 与 `%TEMP%\SonyHeadphonesController.diag.log` 记录设备名与 MAC，不记录音频内容。
- **依赖**：仅 `H.NotifyIcon.Wpf`；CI 在 `windows-latest` 上以 `TreatWarningsAsErrors` 构建，产物由 GitHub Actions 打包发布，请核对下载来源。

## 发布产物校验

Release 中的 zip 由 GitHub Actions 构建产出。若需自行校验，可克隆对应 tag 后本地执行 `dotnet publish` 比对。
