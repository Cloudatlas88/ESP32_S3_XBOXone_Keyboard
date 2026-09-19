/**
 * @file app_config.h
 * @brief 全局常量 —— 单一事实来源
 *
 * 克隆目标（实测读取自本机实体键盘接收器 A）：
 *   VID:PID   = 3554:FA09
 *   bcdDevice = 0x2007  (REV_2007)
 *   iProduct  = "2.4G Wireless Receiver"
 *   iSerial   = 无  <-- 关键：本设备同样不报告序列号
 */
#pragma once

/* ───────────────── 版本 ───────────────── */
#define APP_NAME            "main_fw"
#define FW_VERSION_STR      "Ver001"
#define KB_PROTO_VERSION    0x01        /* 配置通道协议版本（阶段二用）*/

/* ───────────────── 克隆目标：接收器 A ───────────────── */
#define KB_VID              0x3554
#define KB_PID              0xFA09
#define KB_BCD_DEVICE       0x2007
/* iManufacturer / iProduct —— 实测读自真实接收器（hidapi manufacturer_string / product_string）*/
#define KB_STR_MANUFACTURER "Compx"
#define KB_STR_PRODUCT      "2.4G Wireless Receiver"

/* ───────────────── USB 接口/端点分配 ───────────────── */
#define KB_ITF_KEYBOARD     0           /* MI_00 —— 被 Windows kbdhid 独占 */
#define KB_ITF_CONFIG       1           /* MI_01 —— 厂商自定义，上位机可访问 */

#define KB_EP_KEYBOARD_IN   0x81
#define KB_EP_CONFIG_IN     0x82

#define KB_EP_KEYBOARD_SIZE 8           /* 引导键盘报告固定 8 字节 */
#define KB_EP_CONFIG_SIZE   64
#define KB_EP_INTERVAL_MS   1           /* 1ms 轮询，配合 1ms 扫描周期 */

/* ───────────────── 配置通道（阶段二实现协议）─────────────────
 * 报文帧 64 字节 = Report ID(1) + 负载(63)
 * 注意：HID 描述符里的 Report Count 指的是「负载」字节数，
 *       Report ID 是额外的 1 字节前缀，不计算在内。
 */
#define KB_CFG_REPORT_ID      0x03      /* Feature Report ID：配置读写（主机发起）*/
#define KB_CFG_REPORT_SIZE    64        /* 线上帧总长（含 Report ID）*/
#define KB_CFG_PAYLOAD_SIZE   63        /* 负载长度 = 64 - 1，写进报告描述符 */

/* ───────────────── 输入状态实时上报通道 ─────────────────
 * 厂商接口的 Input Report，由设备主动推送（走中断 IN 端点），
 * 供上位机「按键布局」页实时显示摇杆/按钮状态。
 *
 * ★ 帧布局（54 字节）见文件末尾的 IN_OFF_* 偏移表 —— 那里是唯一事实来源，
 *   不要再在这里写一份长度，否则两处会不一致（这次就踩过：
 *   这里留了旧的 32，末尾又定义了 52，直接编译报重定义）。
 */
#define KB_RID_INPUT          0x04      /* Input Report ID */
#define KB_INPUT_INTERVAL_MS  50        /* 20Hz 上报 */

/* ───────────────── 输入采集（★ 24 槽位大改造 2026-09-15）─────────────────
 *
 *   配置格式 0x0004 / **572 字节**（26 键位 + 2 摇杆），
 *   上报帧 **54 字节**（槽位位图 4 字节、方向组 3 组、右摇杆、四轴行程）。
 *   2026-09-15 从 24/556/52 扩到 26/572/54 —— 追加了两个扳机槽位。
 *
 *   规格见 项目结构.md §六 —— 固件与上位机按同一份契约实现。
 *   引脚权威数据见 hardware_ref/xbox_pcb/pinmap.csv
 *   （tools/check_pinmap.py 可机器校验：重复 / 禁用脚 / ADC 通道匹配）。
 *
 *   ★ 引脚是**按实际焊接**定的，不是照抄哪份表：
 *     十字键与 R3 于 2026-09-14 整体前移过一格（原 R3=17、十字键 18/21/38/39，
 *     实际焊成十字键 17/18/21/38），R3 挪到空出来的 39。
 */
#define SLOT_COUNT            26

/* ── 槽位编号 ──
 * ★★ 必须与上位机 layout.json 的 slots 数组**完全一致**。
 *    固件按这个顺序打包 buttons[3]，上位机按同一个顺序解包。
 */
typedef enum {
    SLOT_A = 0, SLOT_B, SLOT_X, SLOT_Y,
    SLOT_LB, SLOT_RB,
    SLOT_VIEW, SLOT_MENU, SLOT_XBOX, SLOT_SHARE,
    SLOT_L3, SLOT_R3,
    SLOT_DPAD_UP, SLOT_DPAD_DOWN, SLOT_DPAD_LEFT, SLOT_DPAD_RIGHT,
    SLOT_LS_UP,   SLOT_LS_DOWN,   SLOT_LS_LEFT,   SLOT_LS_RIGHT,
    SLOT_RS_UP,   SLOT_RS_DOWN,   SLOT_RS_LEFT,   SLOT_RS_RIGHT,

    /* ★ 扳机（2026-09-15 追加）—— 放在**末尾**，不动已有编号。
     *
     * 它们是**数字输入**：Xbox 扳机是电位器/霍尔，抽头电压从 0V 扫到 3.3V，
     * 而 GPIO40/41 **不是 ADC 引脚**（ESP32-S3 只有 ADC1=GPIO1-10、
     * ADC2=GPIO11-20，见 soc/adc_channel.h），所以读不了模拟量。
     * 接成数字脚后，抽头电压扫过 ~1.65V 时逻辑翻转 ——
     * 也就是**行程过半判为「按下」**。
     *
     * 对键盘模拟器来说这完全够用：扳机最终也只是映射成一个键，
     * 只需要"按下/没按下"这个二值信号，不需要行程量。
     * 想要真实模拟行程就得重焊到 ADC 脚上（已与用户确认过，选择了不焊）。 */
    SLOT_LT, SLOT_RT,
    SLOT_MAX
} slot_idx_t;

_Static_assert(SLOT_MAX == SLOT_COUNT, "槽位枚举与 SLOT_COUNT 不一致");

/* ── 数字输入引脚（18 个，含两个扳机）── */
#define BTN_GPIO_A            6
#define BTN_GPIO_B            7
#define BTN_GPIO_X            8
#define BTN_GPIO_Y            9
#define BTN_GPIO_LB           10
#define BTN_GPIO_RB           11
#define BTN_GPIO_VIEW         12
#define BTN_GPIO_MENU         13
#define BTN_GPIO_XBOX         14
#define BTN_GPIO_SHARE        15
#define BTN_GPIO_L3           16
#define BTN_GPIO_R3           39
#define BTN_GPIO_DPAD_UP      17
#define BTN_GPIO_DPAD_DOWN    18
#define BTN_GPIO_DPAD_LEFT    21
#define BTN_GPIO_DPAD_RIGHT   38
/* ★ 扳机：数字输入（原表里标"保留不接"，用户实际接在这里）*/
#define BTN_GPIO_LT           40
#define BTN_GPIO_RT           41

/* ── 模拟输入：4 路（2 个摇杆 × 2 轴）──
 * 只用 ADC1。ADC2 在开 WiFi 时会冲突，留条后路。
 */
#define ADC_GPIO_LX           1         /* ADC1_CH0  左摇杆 X */
#define ADC_GPIO_LY           2         /* ADC1_CH1  左摇杆 Y */
#define ADC_GPIO_RX           4         /* ADC1_CH3  右摇杆 X */
#define ADC_GPIO_RY           5         /* ADC1_CH4  右摇杆 Y */

/* 摇杆定义（索引即 kb_config_t.stick[] 的下标）*/
#define STICK_L               0
#define STICK_R               1
#define STICK_COUNT           2

#define SCAN_PERIOD_MS        1         /* 扫描周期 */
#define ADC_SAMPLES_PER_SCAN  8         /* 每周期每轴采样 8 次取中位数 */
#define KEY_DEBOUNCE_COUNT    5         /* 连续 5 次稳定才确认（=5ms）*/
#define STICK_CENTER_SAMPLES  200       /* 开机自校准：采样 200 次求中位作为中心 */
#define STICK_DEADBAND_RAW    6         /* 原始值死区，抗 ADC 噪声 */
#define STICK_DEADZONE_PCT    12        /* 默认死区 12%（需求书 10~15%）*/
#define STICK_DEADZONE_MIN    0         /* 配置允许的最小死区（0 = 不设死区）*/
#define STICK_DEADZONE_MAX    60        /* 配置允许的最大死区 */
#define STICK_HYSTERESIS_PCT  4         /* 迟滞，防止边界抖动 */
#define STICK_FAULT_COUNT     300       /* 连续贴轨多少次判定为传感器故障 */

/* NVS 写入频率限制：一分钟最多落盘一次（需求书 §4.5 磨损控制）*/
#define NVS_WRITE_MIN_INTERVAL_MS   60000

/* ───────────────── 宏引擎 ───────────────── */
#define MACRO_TIMEOUT_MS      30000     /* 宏总超时（需求书 §4.4：30 秒强制终止）*/
#define MACRO_RAND_DEFAULT_BASE_MS 100  /* 随机延迟基准值缺省（宏步骤里填 0 时用）*/
#define MACRO_TASK_PERIOD_MS  5         /* 状态机步进周期 */
#define MACRO_TAP_HOLD_MIN_MS 18        /* 单键敲击的最短保持（太短主机采不到）*/
#define MACRO_TAP_HOLD_MAX_MS 35

/* 按钮 ID 已由上面的 slot_idx_t 取代（24 槽位，名字即 Xbox 按键名）*/

/* 方向位图 */
#define DIRBIT_UP     0x01
#define DIRBIT_DOWN   0x02
#define DIRBIT_LEFT   0x04
#define DIRBIT_RIGHT  0x08

/* ── Input Report（RID 0x04）帧布局 —— 32 → 52 字节 ──
 * ★ 固件与上位机 InputState.cs 必须逐字节一致。
 *   偏移写在这里而不是散在 main.c 里，是为了改的时候两边好对。
 */
#define KB_INPUT_PAYLOAD      54        /* 负载长度 */
#define IN_OFF_FLAGS           0        /* 1    bit0 数据有效 / bit1 USB已挂载 / bit2 运行期贴轨 */
#define IN_OFF_BUTTONS         1        /* 4    ★ 26 个槽位，bit0 = 槽位 0 */
#define IN_OFF_DIRS_DPAD       5        /* 1    十字键方向位 */
#define IN_OFF_DIRS_LSTICK     6        /* 1    左摇杆方向位 */
#define IN_OFF_DIRS_RSTICK     7        /* 1    右摇杆方向位 */
#define IN_OFF_STAT            8        /* 1    bit1 = 有宏正在执行 */
#define IN_OFF_RAW             9        /* 8    4 轴原始值 x,y,rx,ry（各 uint16）*/
#define IN_OFF_CENTER         17        /* 8    4 轴中心 */
#define IN_OFF_UPTIME         25        /* 4 */
#define IN_OFF_SEQ            29        /* 4 */
#define IN_OFF_AXIS_FAULT     33        /* 1    bit0..3 = 四轴故障 */
#define IN_OFF_BTN_RAW        34        /* 4    ★ 26 个槽位的原始 GPIO 电平（未防抖）*/
#define IN_OFF_TRAVEL         38        /* 16   ★ 四轴行程极值：x, y, rx, ry 各 min/max */

/* ★★ 每一段的边界都钉死。
 *   只断言最后一项是不够的 —— 第一版把 RAW 写成 16 字节（实际 4 轴只有 8 字节），
 *   后面的 CENTER/UPTIME/SEQ/AXIS_FAULT 全部错位，而"最后一项对不对"照样通过。
 *   这就是"断言挑最省事的那条写"的典型漏洞。
 *   （槽位从 24 扩到 26 时，BUTTONS 和 BTN_RAW 都从 3 字节变 4 字节，
 *     这几条断言正是防止漏改其中一处。） */
_Static_assert(SLOT_COUNT <= 32,                          "槽位超过 4 字节位图的容量（32 位）");
_Static_assert(IN_OFF_BUTTONS     + 4 == IN_OFF_DIRS_DPAD,  "flags/槽位段长度不对（26 槽位应为 4 字节）");
_Static_assert(IN_OFF_STAT        + 1 == IN_OFF_RAW,        "方向/状态段长度不对");
_Static_assert(IN_OFF_CENTER      + 8 == IN_OFF_UPTIME,     "raw/center 段长度不对");
_Static_assert(IN_OFF_UPTIME      + 4 == IN_OFF_SEQ,        "uptime 段长度不对");
_Static_assert(IN_OFF_SEQ         + 4 == IN_OFF_AXIS_FAULT, "seq 段长度不对");
_Static_assert(IN_OFF_AXIS_FAULT  + 1 == IN_OFF_BTN_RAW,    "axis_fault 段长度不对");
_Static_assert(IN_OFF_BTN_RAW     + 4 == IN_OFF_TRAVEL,     "btn_raw 段长度不对（26 槽位应为 4 字节）");
_Static_assert(IN_OFF_TRAVEL      + 16 == KB_INPUT_PAYLOAD, "行程段之后还有剩余字节，或偏移表与负载长度不符");

/* ───────────────── 定稿版本号 ─────────────────
 * ★ Ver001 = 第一版定稿。
 *   上位机把这三个字节显示成 "Ver001"（值 = 主×100 + 次×10 + 修订），
 *   所以 Ver001 / Ver012 / Ver123 都能表示 —— 比 "0.1.0" 这种三段式好认。
 *   ★ 改版本时**三个宏一起改**，上位机的显示会跟着变，不用改上位机代码。
 */
#define FW_VER_MAJOR   0
#define FW_VER_MINOR   0
#define FW_VER_PATCH   1

/* ───────────────── 键盘报告 ───────────────── */
/* 接口 0 的报告描述符不使用 Report ID（为了兼容 BIOS 引导协议）*/
#define KB_HID_RID_KEYBOARD 0

/* HID modifier 位掩码 */
#define MOD_LCTRL           0x01
#define MOD_LSHIFT          0x02
#define MOD_LALT            0x04
#define MOD_LGUI            0x08
#define MOD_RCTRL           0x10
#define MOD_RSHIFT          0x20
#define MOD_RALT            0x40
#define MOD_RGUI            0x80

/* ───────────────── 调试用按键 ───────────────── */
/* 板上 BOOT 键，按下发一个 'a'，用于验证键盘接口真的能打字 */
#define BOOT_BUTTON_GPIO    0
