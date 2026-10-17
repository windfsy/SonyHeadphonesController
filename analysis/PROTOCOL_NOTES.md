# Sony WF-1000XM5 蓝牙控制协议 — 逆向分析结果

来源：Sound Connect (Android) v13.3.0 APK 反编译（jadx），与社区逆向成果交叉验证。
分析日期：2026-10-17。反编译源码位于 `analysis/jadx_out/sources/`。

## 1. 传输层

**经典蓝牙 RFCOMM（SPP）**，由 `ep0/c.java` 确认：
`BluetoothDevice.createRfcommSocketToServiceRecord(uuid)` — Android 标准 SPP 连接，无需额外握手。

SDP 服务 UUID（`lm0/i.java`，v2 优先）：

| UUID | 用途 |
|---|---|
| `956C7B26-D49A-4BA8-B03F-B17D393CB6E2` | v2 MDR 服务（WF-1000XM5 / XM5 代及更新） |
| `96CC203E-5068-46AD-B32D-E316F5E069BA` | v1 MDR 服务（XM4 及更老，兜底） |
| `5B833E20-6BC7-4802-8E9A-723CECA4BD8F` | BLE GATT TANDEM_V2_HPC_SERVICE（BLE 路径，`ServiceUuid.java`） |

**无加密、无鉴权**（应用层）。链路安全由蓝牙配对层承担。

## 2. 帧格式（`qm0/b.java` = 官方编码器）

```
0x3E | ESC( <DataType:1> <Seq:1> <Len:BE32> <Payload> <Checksum:1> ) | 0x3C
```

- 起始 `0x3E`（62），结束 `0x3C`（60）
- 转义（`cp0/b.java` + `qm0/e.java`）：载荷中 `0x3C`→`3D 2C`，`0x3D`→`3D 2D`，`0x3E`→`3D 2E`；接收端遇 `3D` 取下一字节 `| 0x10` 还原
- 校验和（`cp0/f.java`）：**sum8** —— 转义前从 DataType 到 Payload 末字节逐字节累加取低 8 位
- DataType（`DataType.java`）：`DATA_MDR=0x0C`（table1 命令）、`DATA_MDR_NO2=0x0E`（table2 命令）、`ACK=0x01`、`LARGE_DATA_MDR=0x2C` 等
- ACK：接收方对每个需确认的帧回 ACK，seq = `1 - 收到的seq`（`qm0/b.java` 中预计算的两条 ACK：`3E 01 00 00000000 00 3C` 和 `3E 01 01 00000000 00 3C`）
- 大于 MTU 的帧按块拆分发送（`cp0/a.java`）
- 解析端（`qm0/a.java`）行为与编码端互逆，校验失败丢帧

## 3. 命令层（MDR v2）

两级结构：`DataType` 选择命令表（table1=`DATA_MDR`，table2=`DATA_MDR_NO2`），payload 首字节是命令码。

### 3.1 连接初始化序列（`vv/e.java`）

1. 发 `CONNECT_GET_PROTOCOL_INFO`（table1）：payload `00 00`
2. 收 `CONNECT_RET_PROTOCOL_INFO`（`in0/k.java`）：payload `01 00 <版本:BE32> <2字节EnableDisable>`（共 8 字节）→ 判定 v2 设备
3. `CONNECT_GET_CAPABILITY_INFO` → `CONNECT_RET_CAPABILITY_INFO`（设备支持的能力表，`in0/e` + `table1/a`）
4. `CONNECT_GET_DEVICE_INFO`（type=MODEL_NAME/SERIES_AND_COLOR/FW_VERSION/INSTRUCTION_GUIDE）→ 各 RET

### 3.2 电量（table1，`yn0/`）

| 方向 | payload | 含义 |
|---|---|---|
| GET | `22 00` | 单体电量 |
| GET | `22 01` | 左右耳电量 |
| GET | `22 02` | 充电盒电量 |
| RET | `23 <type> <L:1> <chgL:1> <R:1> <chgR:1>` | 左右耳：level 0-100 + BatteryChargingStatus（0=未充电 1=充电中 2=未知 3=已充满） |
| RET | `23 00 <lvl> <chg>` | 单体 |
| NTFY | `25 ...` | 电量变化通知（0x25=POWER_NTFY_STATUS） |

命令码：`POWER_GET_STATUS=0x22`，`POWER_RET_STATUS=0x23`，`POWER_NTFY_STATUS=0x25`（`Command.java`）。

### 3.3 ANC / 环境声（table1，`sn0/`）

命令码：`NCASM_GET_STATUS=0x62`，`NCASM_RET_STATUS=0x63`，`NCASM_SET_STATUS=0x64`，`NCASM_NTFY_STATUS=0x65`，`NCASM_GET_PARAM=0x66`，`NCASM_RET_PARAM=0x67`，`NCASM_SET_PARAM=0x68`，`NCASM_NTFY_PARAM=0x69`。

**推荐用 SET_PARAM（0x68），InquiredType = NC_MODE_SWITCH_AND_ASM_SEAMLESS (0x14)**（`sn0/i1.java`）：

```
68 14 01 00 <NcAsmMode> <NcValue> <AmbientSoundMode> <Volume>
```

| 字节 | 含义 | 取值 |
|---|---|---|
| `0` | 命令 | `0x68` |
| `1` | InquiredType | `0x14`（MODE_SWITCH_AND_ASM_SEAMLESS） |
| `2` | ValueChangeStatus | `0x01`=CHANGED（0=UNDER_CHANGING） |
| `3` | NcAsmOnOffValue | `0x00`=OFF, `0x01`=ON |
| `4` | NcValue | `0`=OFF, `1`=NC单, `2`=NC双, `3`=AUTO, `4`=AUTO单, `5`=AUTO双 |
| `5` | AmbientSoundMode | `0`=NORMAL, `1`=VOICE |
| `6` | ASM Volume | 0-255（实际 0-20，`un0/l.java` 校验 0..255） |

帧总长 7 字节（`un0/l.d()` 校验 `length==7`）。回复走 RET_PARAM 0x67 / NTFY_PARAM 0x69 同布局。
（社区 mos9527 实现为 `68 17 01 ...`，InquiredType 0x17=NC_MODE_SWITCH_AND_ASM_ON_OFF，同样有效——0x14 与 0x17 是同一族两种形态。）

### 3.4 语音提示音量（table2 → `DATA_MDR_NO2`，`ap0/`）

注意：这是"语音指导/提示音量"（voice guidance volume，-2..2 五档），不是媒体音量。媒体音量由耳机自身 AVRCP/绝对音量跟随系统，Sound Connect 也只调这个。

| 方向 | payload | 含义 |
|---|---|---|
| GET | `46 20` | VOLUME (0x20) 查询 |
| RET | `47 20 <vol>` | 当前档位（`ap0/b0.java`，长度 3） |
| SET | `48 21 <vol:1> <onOff>` | `VOICE_GUIDANCE_SET_PARAM=0x48`，VOLUME_SETTING_FIXED_TO_5_STEPS=0x21，vol 范围 -2..2（写时 +256 取补码，`e.k()`），onOff `0x00`=ON `0x01`=OFF（`ap0/l0.java`，长度 4） |

命令码（table2 `Command.java`）：`VOICE_GUIDANCE_GET_PARAM=0x46`，`RET_PARAM=0x47`，`SET_PARAM=0x48`，`NTFY_PARAM=0x49`。

**重要**：table2 命令必须封装在 `DATA_MDR_NO2 (0x0E)` 帧中发送，table1 命令用 `DATA_MDR (0x0C)`。

## 4. v2 命令码总表（table1，`Command.java`）

CONNECT 0x00-0x07 / GET_TEST 0x0F / COMMON 0x10-0x19 / POWER 0x20-0x29 / UPDT(FOTA) 0x30-0x3F / LEA(LE Audio) 0x40-0x4D / EQEBB(EQ) 0x50-0x5B / NCASM 0x60-0x69 / SENSE 0x70-0x7B / OPT 0x80-0x89 / ALERT 0x90-0x99 / PLAY 0xA0-0xA9 / SAR_AUTO_PLAY 0xB0-0xB9 / LOG 0xC4-0xC9 / GENERAL_SETTING 0xD0-0xD9 / AUDIO 0xE0-0xE9 / SYSTEM 0xF0-0xFD

每族内：GET_CAPABILITY=n, RET=n+1, GET_STATUS=n+2, RET=n+3, SET_STATUS=n+4, NTFY_STATUS=n+5, GET_PARAM=n+6, RET_PARAM=n+7, SET_PARAM=n+8, NTFY_PARAM=n+9。

## 5. 证据文件索引（jadx 输出，相对 `analysis/jadx_out/sources/`）

| 内容 | 文件 |
|---|---|
| SDP UUID（v2/v1） | `lm0/i.java` |
| BLE 服务 UUID 表 | `com/sony/songpal/ble/client/ServiceUuid.java` |
| RFCOMM SPP 连接 | `ep0/c.java`、`dp0/a.java` |
| 帧编码器（含 ACK 预计算） | `qm0/b.java` |
| 帧解码器 | `qm0/a.java` |
| 转义/解转义 | `cp0/b.java`、`qm0/e.java` |
| sum8 校验 | `cp0/f.java` |
| 分块 | `cp0/a.java` |
| DataType 枚举 | `com/sony/songpal/tandemfamily/message/DataType.java` |
| table1 命令枚举 | `com/sony/songpal/tandemfamily/message/mdr/v2/table1/Command.java` |
| table2 命令枚举 | `com/sony/songpal/tandemfamily/message/mdr/v2/table2/Command.java` |
| table1/2 路由（kmp 层） | `com/sony/songpal/mdr/platform/connection/connection/bridge/TandemMdrTransportImpl.java` |
| 初始化流程 | `vv/e.java` |
| 协议信息 GET/RET | `in0/c.java`、`in0/k.java` |
| 电量 GET/RET/解析 | `yn0/c.java`、`yn0/g0.java`、`yn0/r0.java`、`yn0/q0.java`、`yn0/j0.java` |
| ANC SET_PARAM 全家 | `sn0/z0.java`、`sn0/i1.java`、`un0/l.java` |
| ANC 参数枚举 | `com/sony/songpal/tandemfamily/message/mdr/v2/table1/ncasm/param/`（NcAsmInquiredType/NcAsmMode/NcAsmOnOffValue/NcValue/AmbientSoundMode/ValueChangeStatus） |
| 音量 RET/SET | `ap0/x.java`、`ap0/b0.java`、`ap0/h0.java`、`ap0/l0.java` |
| 语音指导 InquiredType | `com/sony/songpal/tandemfamily/message/mdr/v2/table2/voiceguidance/param/VoiceGuidanceInquiredType.java` |
| 音量写入封装 | `b70/v.java` |

## 6. 对 Windows 实现的直接影响

1. 用 `AF_BTH + BTHPROTO_RFCOMM` socket，`SOCKADDR_BTH` 填 v2 UUID（Windows 栈自动 SDP 解析）——与官方 `createRfcommSocketToServiceRecord` 等价
2. 帧codec按 §2 直译成 C#（<100 行）
3. 连接后先发 `00 00` INIT，收到 8 字节 `01 ...` RET 即确认 v2 设备
4. table2 命令（音量）务必用 `DATA_MDR_NO2` 帧类型
5. 音量范围 -2..2（写入时是补码字节），UI 上映射为 5 档
