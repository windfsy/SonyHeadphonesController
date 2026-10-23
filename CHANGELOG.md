# 更新日志

本项目的所有重要变更将记录在此文件中。

格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### 新增

（暂无）

## [0.1.3] - 2026-10-23

面板视觉重整 + 协议健壮性加固：外观与交互是本版主线，同时修掉四处解析与 IO 缺陷。

### 新增

- **面板视觉重整**：ANC 三态改为分段滑块（滑动动画过渡，替代生硬跳位；命中区扩至整段，去掉点击后的虚线焦点框），
  控件透明化 + 莫奈色板，毛玻璃底色降至 205 并以 80% 不透明度铺底
- **关注语音开关**：滑钮平移 + 轨道淡入的代码驱动动画；关闭态加深底色并加细边框（半透明白叠白底对比度不足），
  滑钮投影与 ANC 滑钮同参数
- **托盘 tooltip**：已连接态显示多行电量（左耳 / 右耳 / 充电盒），不打开面板也能看到

### 修复

- **帧缓冲无上限（内存耗尽）**：对端用 `0x3E` 开启一帧后若迟迟不发 `0x3C`，读线程的累积缓冲会无限增长；
  现按 `Frame.MaxEscapedFrameBytes`（≈8 KB，为载荷上限转义后的最坏情况）封顶，超限丢弃半帧并等下一个起始标记重同步
- **长度字段符号溢出**：`Frame.Decode` 原先用 int 拼接 4 字节长度，最高位为 1 时溢出为负，
  `6 + declaredLen + 1` 的边界判断随之失效并以负索引访问缓冲；改为 long 读取，并新增 `MaxPayloadBytes`（4 KB）上限，
  超限按 `FormatException` 处理（由上层 catch 记录日志，不再依赖异常类型兜底）
- **`BthSocket.Send` 部分发送重发已发字节**：重试时未推进偏移（`send` 的 P/Invoke 只有数组头地址），
  底层只接受部分字节时会把已发出的字节再发一遍，导致帧损坏；改为传裸地址并随累计字节数推进
- **配置写入非原子**：`AppConfig.Save` 改为写临时文件后原子替换，避免进程在写盘途中退出留下半截 JSON
  （`Load` 会静默丢弃损坏配置，代价是用户重选设备）
- **毛玻璃快照恢复不透明画笔**：半透明画笔会让未模糊的实时画面透回来，把自适应提白压成平灰

### 变更

- **常驻内存大幅下降**：空闲工作集 110–120MB → 25–40MB（实测真机连接态）
  - **托盘图标按需重绘**：15s 兜底轮询即使状态无变化也会重走整套 DrawingVisual→RenderTargetBitmap→PNG 编码→写盘→重读管线（空闲期最大分配源）；现按 (文本,颜色) 缓存冻结位图，状态不变零分配零 IO，图标更新仅在电量/连接真实变化时发生
  - **空闲工作集回落**：启动稳定后（30s）与面板隐藏后（2s）各做一次 GC 回收 + `SetProcessWorkingSetSize` 归还页面；打开面板触发的内存增长（截屏模糊、渲染栈）在面板收起后随之回落
  - 实测：空闲连接态不再出现每 15s 的内存锯齿与缓慢爬升（原 4 分钟 64→71MB）；GC 托管堆稳定 ~1MB
- 删除 `BthSocket` 中未使用的 `select` / `fd_set` / `TimeVal` / `ioctlsocket` / `FIONBIO` / `BuildFdSet`
  （`BuildFdSet` 的 `AllocHGlobal` 没有配对释放，留着是隐患）
- CI / Release 的 Actions 全部按 commit SHA 钉死（checkout v4.4.0、setup-dotnet v4.3.1、upload-artifact v4.6.2），
  仓库侧开启 `sha_pinning_required` 与「仅允许 GitHub 官方」的 Actions 白名单
- CI 显式声明 `permissions: contents: read`（最小权限）
- Release 新增 `.sha256` 校验文件并与 zip 同批上传——不可变发布下资产无法补传，校验值只能随包生成

## [0.1.2] - 2026-10-23

品牌与工程化发布：补齐应用图标、README 主图与开源协作配套，功能与协议行为无变更。

### 新增

- **应用图标**：新增 `Assets/app.ico`（16/20/24/32/40/48/64/96/128/256 十档），通过 `ApplicationIcon` 嵌入 exe，
  并作为控制面板与设备选择窗的窗口图标——此前 exe 使用 .NET 默认图标
- **README 主图与徽章**：主图（产品标识 + 一句话介绍）置于 README 开头，补充 release / license / CI / 平台 / 运行时徽章
- **下载安装章节**：说明 zip 获取、免安装运行与配置落点；注明托盘图标为电量徽章（与程序图标不同）的设计取舍
- **工程元数据**：exe 属性面板现显示产品名、版本、描述、作者、版权与仓库地址（`Version` / `Product` / `Description` 等）
- **安全策略**：新增 [SECURITY.md](SECURITY.md)——漏洞上报渠道、威胁模型（明文 RFCOMM 的信任边界、配置与日志范围）
- **PR 模板**：`.github/PULL_REQUEST_TEMPLATE.md`，包含零警告构建与真机验证清单

### 变更

- 版本号 0.1.1 → 0.1.2
- README 重排：功能 → 下载安装 → 运行要求 → 使用 → 排障 → 已知限制 → 开发，并补充许可与商标声明
- Release workflow 的触发 tag 放宽为 `v*` 与纯版本号（`0.1.2`），与历史 release 命名及 CHANGELOG 链接保持一致

## [0.1.1] - 2026-10-19

### 新增

- **面板毛玻璃背景**：分层窗口（AllowsTransparency）拿不到 DWM 系统模糊，采用快照方案——每次打开面板前截取其身后的屏幕区域，C# 分离式盒模糊（≈高斯）后铺成卡片背景；按快照平均亮度自适应提白（暗壁纸多提、亮壁纸少提），保证灰色标签文字在任何壁纸上都有对比度；截屏失败（安全桌面等）回退静态底色，失败记录 diag 日志
- **面板视觉打磨**：卡片圆角 10→16，产品图上下边距加大
- **面板顶部产品图**：展示 WF-1000XM5 官方渲染图（Sound Connect 云端资源，背景已去除）
- **产品图按设备颜色动态选择**：通过 CONNECT_GET_DEVICE_INFO (SERIES_AND_COLOR) 读取耳机颜色字节
  （与云端 model_color_id 同编码），黑/银/粉三色自动匹配；未知颜色回退黑色
- **通透「关注语音」开关**：Sound Connect 的 Focus on Voice；NCASM 帧 [5] 位（0=普通 1=关注语音）
  的发送与 NTFY 状态同步，仅通透模式可调，切模式/调等级时保持该状态

### 修复

- **控制面板首次弹出定位到屏幕外**：首帧布局前 `Window.Measure` 返回 (0,0)，据此算出的坐标把窗口推到工作区右下角之外；改为按固定宽度 + 直接测量内容卡片高度定位
- **耳机不在电脑旁时界面永远停在"重连中"**：退避重连循环没有出口；现在 removal 事件与重连 tick 发现 OS 已无链路时落回"未连接"
- **不再抢连耳机**：RFCOMM connect 前必须确认 Windows 已持有到耳机的链路；OS 报告未连接时绝不发起连接（避免把正连着手机的耳机从空中 page 走）
- **单耳使用时缺失耳显示 0%、托盘被拖到 0%**：设备对不在位的耳机报 0%（协议无"不存在"哨兵值，Sound Connect HCI 抓包确认官方收到的也是 0）；现归一化为"未知"（--），托盘电量只取在用耳；耳机放回盒中后电量正确翻为 --
- **ANC 点击后立即跳回"关闭"**：SET 后的 GET 回读在过渡期/未佩戴场景会返回 OFF，覆盖 NTFY 已确认的正确状态；改为与 Sound Connect 一致——SET 后只信任 NTFY 推送，不再回读
- **通透等级滑条不可交互、拉满仍留灰段**：Track 模板的填充按钮停在不可见 thumb 宽度之前（拉满 20/20 右端仍有灰段），
  且可点按区域只有 10px 轨道带；改为覆盖层填充（按值比例铺满全宽）+ 面板级点按/拖动处理（全条可点，按住即拖动）
- 构建零警告（消除 `GuardedAsync` 的 CS4014）

### 变更

- **状态同步提速**：兜底刷新 2 分钟 → 15 秒（耳机入盒没有协议通知，只能靠轮询发现）；
  补上 SPEC 里"打开面板时全量拉取"的实现（此前未实现，导致开面板看不到新状态）；
  所有命令经串行闸门排队（协议同一时刻只允许一条在途），兜底刷新与用户操作不再互相误伤
- 诊断日志补充 ANC SET/GET/NTFY 与电量 GET 的状态行（此前状态变化完全不可观测）
- 协议文档补充：缺失耳 0% 语义、佩戴检测（`22 05`）与未佩戴时 NC 自动暂停、InquiredType 0x17 的官方枚举名勘误（实为 DUAL 形态）、SET OFF 时 NTFY 规范化 [4] 字段

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

[Unreleased]: https://github.com/windfsy/SonyHeadphonesController/compare/0.1.3...HEAD
[0.1.3]: https://github.com/windfsy/SonyHeadphonesController/releases/tag/0.1.3
[0.1.2]: https://github.com/windfsy/SonyHeadphonesController/releases/tag/0.1.2
[0.1.1]: https://github.com/windfsy/SonyHeadphonesController/releases/tag/0.1.1
[0.1.0]: https://github.com/windfsy/SonyHeadphonesController/releases/tag/0.1.0
