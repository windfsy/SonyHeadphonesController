# 更新日志

本项目的所有重要变更将记录在此文件中。

格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [Unreleased]

## [0.1.0] - 2026-10-17

MVP 首个发布版。

### 新增

- **系统托盘常驻**：托盘图标实时显示电量（按电量着色：绿/橙/红，断连灰色），悬停显示设备与连接状态
- **自动连接**：耳机连上电脑时自动建立控制通道（蓝牙设备事件驱动 + 30 秒兜底轮询），断开时静默退避重连（1s→5s→30s）
- **控制面板**：左键托盘弹出，仿 Windows 音量弹窗，失焦自动隐藏；入场动画、首次弹出正确锚定任务栏右上角
- **电量显示**：左耳 / 右耳 / 充电盒三体电量 + 充电状态；托盘电量取已知耳的最低值
- **ANC 三态切换**：降噪 / 通透 / 关闭，重新进入通透时记忆上次等级
- **通透等级**：0-20 滑条，拖动结束才发送，避免命令风暴
- **状态同步**：耳机主动推送（NTFY）+ 打开面板全量拉取 + 2 分钟兜底刷新；手机端 Sound Connect 改 ANC，电脑端自动跟上
- **命令防护**：校验 + 单次重试，两次失败显示"耳机无响应"提示
- **首次运行设备选择**：列出 Windows 已配对的蓝牙设备，选中后自动保存配置

### 逆向成果

- 基于 Sound Connect (Android) v13.3.0 APK 的逆向分析，实现 Sony MDR v2 蓝牙协议（明文 RFCOMM 通道），协议细节见 [docs/PROTOCOL_NOTES.md](docs/PROTOCOL_NOTES.md)

[Unreleased]: https://github.com/wind.sy/SonyHeadphonesController/compare/0.1.0...HEAD
[0.1.0]: https://github.com/wind.sy/SonyHeadphonesController/releases/tag/0.1.0
