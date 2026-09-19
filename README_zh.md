# ESP32-S3 Xbox One 手柄转键盘适配器

[English README](README.md)

**基于 ESP32-S3 (N16R8) + TinyUSB + FreeRTOS 将 Xbox One 手柄改装为 USB HID 键盘**

> **定稿版本：Ver001** —— 包含完整固件、上位机配置工具、硬件参考文档的正式发布版

---

## 项目简介

本项目通过将 Xbox One 手柄主板替换为 ESP32-S3 模块，实现手柄到 USB HID 键盘的转换。固件扫描 26 个按键输入（含双模拟摇杆 ADC 采集），按配置映射为键盘按键码，并提供 Windows 上位机进行完整配置。

### 核心特性

- **26 个输入槽位**：A/B/X/Y、LB/RB、View/Menu/Xbox/Share、L3/R3、十字键(4)、左摇杆(4)、右摇杆(4)、LT/RT
- **双模拟摇杆**：4 通道 ADC1（1ms 周期、中位数滤波、死区/迟滞/自校准）
- **双 HID 接口**：引导键盘 (MI_00) + 厂商配置通道 (MI_01)
- **宏引擎**：4 个宏 × 20 步，非阻塞状态机（5ms 步进）
- **NVS 持久化配置**：双槽位 + CRC，限频写入（1 分钟 1 次）
- **实时输入上报**（20Hz）供 UI 实时可视化
- **自包含 Windows 配置工具**：单文件 EXE (~100MB)，零安装即用

---

## 仓库结构

```
ESP32_S3_XBOXone_Keyboard/
├── release/Ver001/              ★ 正式发布包（固件 + EXE）
├── firmware/
│   └── main_fw/                 主固件工程 (ESP-IDF)
├── host/
│   ├── KbConfigurator/          C# WinForms 配置工具 (.NET 10)
│   └── native/                  hidapi 原生库
├── tools/                       构建/烧录/验收/发布脚本
├── hardware_ref/
│   └── xbox_pcb/                PCB 照片、引脚表、焊点标注
├── .gitignore
├── eim_config.toml              ESP-IDF 工具链路径配置
├── ESP32-S3.text                硬件参考笔记
├── 启动上位机.cmd               上位机快速启动脚本
├── README.md                    英文文档
├── README_zh.md                 中文文档（本文件）
└── LICENSE                      MIT 许可证
```

> **注意**：构建产物（`build/`、`managed_components/`、`bin/`、`obj/`）已通过 `.gitignore` 排除。`managed_components/` 已入库以支持离线构建。

---

## 固件 (`firmware/main_fw/`)

### 构建系统
- **ESP-IDF v5.x** (测试通过 v5.3+)
- `CMakeLists.txt` 中固定 `PROJECT_VER = "Ver001"`
- 自定义分区表：NVS 64KB（双槽配置）、factory 应用

### 核心源文件 (`main/`)

| 文件 | 职责 |
|------|------|
| `app_config.h` | **唯一事实来源** —— 所有常量、引脚分配、槽位枚举、上报帧偏移、版本宏 |
| `main.c` | 入口、启动流程、HID 发送任务(5ms)、输入上报任务(20Hz)、状态日志 |
| `input_scan.c/h` | 18 路数字输入 + 4 路 ADC（双摇杆），1ms 周期，去抖、自校准 |
| `keymap.c/h` | 槽位按下 → 按键按下/释放（引用计数、6 键无冲突保护） |
| `hid_keyboard.c/h` | 引导键盘 HID 报文（修饰键引用计数、溢出处理） |
| `hid_config.c/h` | 厂商 Feature Report 协议（分片读写、CRC16） |
| `kb_config.c/h` | 配置结构体（956 字节，v0x0006）、默认值、校验、CRC 封装 |
| `nvs_config.c/h` | NVS 双槽存储、延迟批量写入、基于 epoch 的重载 |
| `macro_engine.c/h` | 4 宏 × 20 步，非阻塞，敲击/按住/延迟/随机延迟/组合键 |
| `crc16.c/h` | CRC-16/CCITT-FALSE |
| `usb_descriptors.c/h` | 双 HID 描述符（引导键盘 + 厂商 `0xFF00`） |

> ⚠️ **修改常量请先改 `app_config.h`** —— 槽位数、引脚、上报偏移、版本号均在此。改完运行 `python tools/check_consistency.py` 验证 C/C#/Python 三端一致。

---

## 上位机配置工具 (`host/KbConfigurator/`)

**C# WinForms (.NET 10, x64, 自包含单文件)**

### 架构
```
KbConfigurator/
├── Program.cs              入口 + 测试模式 (--selftest, --layout, --xlayout 等)
├── MainForm.cs             主窗口、工具栏、状态栏
├── Hid/                    KbDevice (HID 读写)、HidNative (P/Invoke)
├── Protocol/               KbConfig、InputState、SlotMap、ConfigSession、Crc16
├── Model/                  KeyCodes (HID 键表)、XboxLayout (layout.json)
├── Ui/                     DarkTheme、控件工厂、DiagramMap、KeyCombo
├── Views/                  ButtonLayoutPanel、MacroPanel、DiagramView、SlotMapPopup
├── Assets/                 手柄示意图 + layout.json + 出厂坐标备份
└── *Test.cs                7 套验收测试（见下表）
```

### 验收测试套件

| 模式 | 测试项 | 需设备 |
|------|--------|--------|
| `--xlayout` | 125 | 否 |
| `--layout` | 43 | 否 |
| `--uilogic` | 51 | 否 |
| `--uitest` | 12 | 否 |
| `--selftest` | 22 | **是** |
| `--guiroundtrip` | 12 | **是** |
| `--shot` | — | 可选（加 `live` 连设备） |

额外：`tools/macro_accept.py` (37 项，需设备)、`tools/check_consistency.py` (10 项，无需设备)。

### 构建
```cmd
dotnet build host\KbConfigurator\KbConfigurator.csproj -c Debug
```
发布（自包含单文件，含原生库）：
```cmd
dotnet publish host\KbConfigurator\KbConfigurator.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

---

## 工具脚本 (`tools/`)

| 脚本 | 用途 |
|------|------|
| `flash.ps1` / `flash.cmd` | **烧录并全归档**：Git 提交 + `flash-NNN` 标签 + `flash_archive/` + `FLASH_LOG.md` 台账 |
| `make_release.py` | 打发布包：`python tools\make_release.py Ver001` |
| `check_consistency.py` | **跨语言常量一致性检查**（固件/C#/Python：槽位数、版本、尺寸、偏移） |
| `check_critical_sections.py` | 静态扫描临界区内是否有阻塞调用 |
| `check_pinmap.py` | 引脚表校验（重复脚/禁用脚/ADC 通道匹配） |
| `check_project.py` | 工程完整性：脚本语法/文档引用/关键文件/单一 .md |
| `macro_accept.py` | 宏功能端到端验收（37 项） |
| `cfg_proto.py` | 配置通道 CLI（`info` / `read` / `set` / `reset` / `demo-macro`） |
| `read_input.py` | 读取一帧上报，判定接线还是软件问题 |
| `bootlog.py` | 抓完整启动日志（`python tools\bootlog.py COM5`） |
| `slot_enable.py` | 临时禁用/启用某槽位（应对引脚被拉低） |
| `gen_xbox_layout.py` + 系列 | 从 PCB 示意图追踪生成 `layout.json` |

---

## 硬件参考 (`hardware_ref/xbox_pcb/`)

| 文件 | 说明 |
|------|------|
| `pinmap.csv` | **权威引脚表**（`check_pinmap.py` 机器校验） |
| `1_主板正面.jpg` ~ `5_摇杆焊点.jpg` | 手柄 PCB 实拍 |
| `annotated_pads.png` | 焊点标注图 |
| `stick_pads_map.png` / `stick_pads_numbered.png` | 摇杆焊点编号 |
| `btnboard_contacts_map.png` | 按键板触点标注 |
| `pads/` | 逐焊点裁剪图 |

---

## 协议契约（固件 ↔ 上位机）

> 固件与上位机的**唯一接口约定**。定义于 `项目结构.md` §六（同步镜像于 `app_config.h`、`kb_config.h`、`KbConfig.cs`、`InputState.cs`）。

### 配置格式 `0x0006` / **956 字节**

| 偏移 | 长度 | 内容 |
|------|------|------|
| 0 | 8 | 包头：magic `0x4B42`、版本、尺寸、CRC16 |
| 8 | 16 | `stick[2]` × 8：死区 / X 反向 / Y 反向 / 保留 5 |
| 24 | 208 | `buttons[26]` × 8：键码 / 修饰键 / trigger(废弃) / flags / macro_id / 保留 3 |
| 232 | 4 | `macro_count` + 保留 3 |
| 236 | 272 | `macros[4]` × 68：4 字节头 + 20 步 × 8 |
| 508 | 64 | 保留 |

- CRC16/CCITT-FALSE 从偏移 8 算到末尾
- `flags`：bit0=启用、bit1=触发宏；`macro_id`=0xFF 表示不使用宏

### 槽位映射（26 槽位）

| 槽位 | 功能 | 槽位 | 功能 |
|------|------|------|------|
| 0-3 | A B X Y | 12-15 | 十字键 ↑ ↓ ← → |
| 4-5 | LB RB | 16-19 | 左摇杆 ↑ ↓ ← → |
| 6-9 | View Menu Xbox Share | 20-23 | 右摇杆 ↑ ↓ ← → |
| 10-11 | L3 R3 | 24-25 | **LT RT**（追加在末尾，原编号不动） |

### 输入上报帧（RID `0x04`）/ **54 字节**

| 偏移 | 长度 | 内容 |
|------|------|------|
| 0 | 1 | flags：bit0 有效 / bit1 USB已挂载 / bit2 摇杆贴轨 |
| 1 | 4 | **26 槽位按下位图** |
| 5 | 3 | 方向位图：十字键 / 左摇杆 / 右摇杆 |
| 8 | 1 | stat：bit1 = 有宏在执行 |
| 9 | 8 | 四轴原始值（LX、LY、RX、RY，各 u16） |
| 17 | 8 | 四轴中心（开机自校准） |
| 25 | 4 | uptime_ms |
| 29 | 4 | 序列号 |
| 33 | 1 | 轴故障掩码（bit0..3） |
| 34 | 4 | **26 槽位原始 GPIO 电平**（未防抖，接线诊断用） |
| 38 | 16 | 四轴行程极值 min/max |

### 配置通道命令（Feature Report RID `0x03`，63 字节负载）

帧头 9 字节：`CMD / SEQ / FLAGS(bit0=LAST) / OFF(u16) / TOTAL(u16) / CRC16(u16)`，后接 54 字节数据。

| 命令 | 值 | 命令 | 值 |
|------|-----|------|-----|
| GET_INFO | 0x01 | MACRO_RUN | 0x08 |
| CFG_READ | 0x02 | STATUS | 0x09 |
| CFG_WRITE | 0x03 | REBOOT | 0x0A |
| CFG_SAVE | 0x04 | ACK | 0x80 |
| CFG_RESET | 0x05 | NACK | 0x81 |
| CALIB_RESET | 0x06 | 0x07 | 已废弃（拒绝执行） |

### USB 标识
- `VID 0x3554 / PID 0xFA09`（克隆自通用 2.4G 接收器，**仅供个人学习使用**）
- **不上报序列号**（`iSerialNumber = 0`）—— 避免 Windows 报 Code 42（重复设备）
- 两个 HID 接口：接口 0 = 引导键盘，接口 1 = 厂商自定义（`UP:0xFF00`）

---

## 构建与烧录

### 环境准备
- ESP-IDF v5.3+（通过 [Espressif IDF 安装器](https://dl.espressif.com/dl/eim/eim.exe)）
- .NET 10 SDK
- Python 3.10+

### 固件编译
```cmd
# 首次需设置目标（会拉取 managed_components，支持离线构建）
idf.py -C firmware/main_fw set-target esp32s3
idf.py -C firmware/main_fw build
```
产物位于 `firmware/main_fw/build/`：`main_fw.bin`、`bootloader/bootloader.bin`、`partition_table/partition-table.bin`。

### 上位机编译
```cmd
dotnet build host\KbConfigurator\KbConfigurator.csproj -c Debug
```

### 烧录（含完整归档）
```cmd
tools\flash.cmd COM5
```
> 烧录后**务必验证设备版本**：`python tools\cfg_proto.py info COM5`

### 发布包制作
```cmd
python tools\make_release.py Ver002 --note "改了什么"
```
输出至 `release/Ver002/`，包含 EXE、固件 bin、flash.cmd。

---

## 设计铁律（踩坑总结）

1. **临界区只碰内存** —— 严禁在锁内打日志、进队列、延时。曾因 `ESP_LOGI` 在锁内导致"循环宏按第二下设备重启"。用 `check_critical_sections.py` 验证。

2. **协议常量三处同步**（C 宏、C# 常量、Python 常量），无编译器对齐。改完跑 `check_consistency.py`。

3. **烧录只走 `tools\flash.cmd`** —— 它会打标签、归档源码、写台账。**烧完必须回头确认设备真报了新版本**（脚本可能报假成功）。

4. **改 UI 必须截图看** —— "文字被裁半截"、"下拉框全黑" 这种断言全绿也复现的问题。

---

## 许可证

本项目采用 **MIT 许可证** —— 详见 [LICENSE](LICENSE)。

> **VID/PID 说明**：USB VID:PID (0x3554:0xFA09) 克隆自通用 2.4G 接收器，**仅供个人学习使用**。商业分发请向 usb.org 申请自有 VID。

---

## 致谢

- [ESP-IDF](https://github.com/espressif/esp-idf) 与 [TinyUSB](https://github.com/hathach/tinyusb) —— 优秀的框架
- [hidapi](https://github.com/libusb/hidapi) —— 跨平台 HID 访问
- Xbox 手柄硬件社区提供的 PCB 参考资料

---

**Ver001** —— 正式定稿。为学习而构建，为复现而文档化。
