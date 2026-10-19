# 贡献指南

感谢关注本项目！欢迎提交 Issue 和 Pull Request。

## 开发环境

- Windows 10/11（x64）
- .NET 10 SDK
- 一副已与电脑配对的 Sony 耳机（WF-1000XM5 及同期机型）用于真机调试

## 构建

```bash
dotnet build SonyHeadphonesController.sln
```

## 发布

```bash
dotnet publish src/SonyHeadphonesController -c Release -o publish
```

framework-dependent 发布，需要目标机器装有 .NET 10 运行时。

正式发版走 GitHub Actions：先更新 CHANGELOG.md（新增 `## [x.y.z]` 小节），然后推 tag 触发：

```bash
git tag vx.y.z && git push origin vx.y.z
```

Release workflow 会校验 tag 与 CHANGELOG 版本一致、构建零警告、打包 zip 并自动创建 GitHub Release。

## 调试

- 托盘应用没有控制台，诊断日志在 `%TEMP%\SonyHeadphonesController.diag.log`
- 蓝牙设备 PnP 属性检查脚本：`docs/inspect-services.ps1 -Mac <设备MAC>`
- 协议细节见 [docs/PROTOCOL_NOTES.md](docs/PROTOCOL_NOTES.md)

## 提交约定

- 提交信息使用 [Conventional Commits](https://www.conventionalcommits.org/zh-hans/) 格式：`feat: ...`、`fix: ...`、`docs: ...`、`chore: ...`
- 每个提交保持自洽、可构建

## Pull Request

1. 从 `main` 拉出功能分支
2. 改动尽量小而聚焦，一个 PR 解决一件事
3. 确保 `dotnet build` 通过、零警告
4. 描述清楚改了什么、为什么、在真机上如何验证的

## 行为准则

保持友善与尊重，专注技术讨论。
