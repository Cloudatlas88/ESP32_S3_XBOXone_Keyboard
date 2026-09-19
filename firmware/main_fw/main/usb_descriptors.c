/**
 * @file usb_descriptors.c
 * @brief USB 描述符定义 —— 双 HID 接口复合设备（拟真 2.4G 无线接收器）
 *
 * 设备树目标（Windows 侧）：
 *   USB\VID_3554&PID_FA09\<端口路径>                USB Composite Device
 *     ├─ USB\VID_3554&PID_FA09&MI_00\...            HID Keyboard Device
 *     └─ USB\VID_3554&PID_FA09&MI_01\...            HID-compliant vendor-defined device
 *
 * ★ 拟真要点（与真实接收器逐项对齐）
 *   · VID/PID      = 3554:FA09
 *   · bcdDevice    = 0x2007
 *   · iManufacturer= 0                    真实设备此项不可读，且复合设备由 usb.inf 兜底
 *   · iProduct     = "2.4G Wireless Receiver"
 *   · iInterface   = 0（两个接口都不设字符串）
 *                    真实设备的所有接口在 hidapi 里都显示设备级产品名，
 *                    说明它没有接口字符串。设为 0 后行为完全一致。
 *   · iSerialNumber= 0                    不报告序列号
 *
 * ★ 关键设计：iSerialNumber = 0
 *   设备不报告序列号 → Windows 用「父设备ID + 端口号」合成实例 ID，
 *   天然按端口唯一 → 与真实接收器插在同一台机器上也不会撞车（不会出现 Code 42）。
 *   若这里写死任何字符串（如 TinyUSB 默认的 "123456"），
 *   两台设备就会产生完全相同的实例 ID → CM_PROB_DUPLICATE_DEVICE (Code 42)。
 */
#include "usb_descriptors.h"
#include "app_config.h"

/* ══════════════════════════════════════════════════════════════
 *  设备描述符
 * ══════════════════════════════════════════════════════════════ */
const tusb_desc_device_t desc_device = {
    .bLength            = sizeof(tusb_desc_device_t),   /* 18 */
    .bDescriptorType    = TUSB_DESC_DEVICE,             /* 0x01 */
    .bcdUSB             = 0x0200,                       /* USB 2.00 */

    /* 类别在接口层定义 → Windows 以 usbccgp 拆成复合设备，
       每个接口（MI_xx）得到独立的设备节点 */
    .bDeviceClass       = 0x00,
    .bDeviceSubClass    = 0x00,
    .bDeviceProtocol    = 0x00,
    .bMaxPacketSize0    = 64,                           /* EP0，全速下合法值之一 */

    .idVendor           = KB_VID,                       /* 0x3554 */
    .idProduct          = KB_PID,                       /* 0xFA09 */
    .bcdDevice          = KB_BCD_DEVICE,                /* 0x2007 */

    .iManufacturer      = 1,    /* "Compx" —— 实测读自真实接收器 */
    .iProduct           = 2,    /* "2.4G Wireless Receiver"       */
    .iSerialNumber      = 0,    /* ★★ 不报告序列号 —— 杜绝 Code 42 ★★ */
    .bNumConfigurations = 1,
};

/* ══════════════════════════════════════════════════════════════
 *  报告描述符
 * ══════════════════════════════════════════════════════════════ */

/**
 * 接口 0：标准 HID 引导键盘
 *
 * 输入报告 8 字节： [modifier][保留][keycode x6]
 * 输出报告 1 字节： [LED 位图]
 *
 * 刻意【不使用 Report ID】—— 这样才能满足 HID 引导协议（Boot Protocol）的要求，
 * 从而在 BIOS/UEFI 环境下也能当普通键盘使用。
 */
const uint8_t hid_report_desc_keyboard[] = {
    0x05, 0x01,        /* Usage Page (Generic Desktop)          */
    0x09, 0x06,        /* Usage (Keyboard)                      */
    0xA1, 0x01,        /* Collection (Application)              */

    /* ── 修饰键：8 个 1-bit 字段 (bit0=LCtrl ... bit7=RGUI) ── */
    0x05, 0x07,        /*   Usage Page (Keyboard/Keypad)        */
    0x19, 0xE0,        /*   Usage Minimum (0xE0 Left Control)   */
    0x29, 0xE7,        /*   Usage Maximum (0xE7 Right GUI)      */
    0x15, 0x00,        /*   Logical Minimum (0)                 */
    0x25, 0x01,        /*   Logical Maximum (1)                 */
    0x75, 0x01,        /*   Report Size (1)                     */
    0x95, 0x08,        /*   Report Count (8)                    */
    0x81, 0x02,        /*   Input (Data,Variable,Absolute)      */

    /* ── 保留字节 ── */
    0x95, 0x01,        /*   Report Count (1)                    */
    0x75, 0x08,        /*   Report Size (8)                     */
    0x81, 0x01,        /*   Input (Constant)                    */

    /* ── LED 输出报告（主机 → 设备）── */
    0x95, 0x05,        /*   Report Count (5)                    */
    0x75, 0x01,        /*   Report Size (1)                     */
    0x05, 0x08,        /*   Usage Page (LEDs)                   */
    0x19, 0x01,        /*   Usage Minimum (Num Lock)            */
    0x29, 0x05,        /*   Usage Maximum (Kana)                */
    0x91, 0x02,        /*   Output (Data,Variable,Absolute)     */
    0x95, 0x01,        /*   Report Count (1)                    */
    0x75, 0x03,        /*   Report Size (3)                     */
    0x91, 0x01,        /*   Output (Constant) —— LED 字节补 3 位 */

    /* ── 6 个键码槽 ── */
    0x95, 0x06,        /*   Report Count (6)                    */
    0x75, 0x08,        /*   Report Size (8)                     */
    0x15, 0x00,        /*   Logical Minimum (0)                 */
    0x25, 0x65,        /*   Logical Maximum (101)               */
    0x05, 0x07,        /*   Usage Page (Keyboard/Keypad)        */
    0x19, 0x00,        /*   Usage Minimum (0)                   */
    0x29, 0x65,        /*   Usage Maximum (101)                 */
    0x81, 0x00,        /*   Input (Data,Array)                  */

    0xC0               /* End Collection                        */
};

/**
 * 接口 1：厂商自定义配置/调试通道
 *
 *   Feature Report (RID 3)，负载 63 字节 —— 主机发起，配置读写，走 EP0 控制传输
 *   Input   Report (RID 4)，负载 KB_INPUT_PAYLOAD 字节（0x0004 起 = 52）——
 *
 * 为什么不放在键盘接口里：Windows 对键盘顶层集合 (UP 0x0001 / U 0x0006)
 * 是【独占】认领的（kbdhid.sys），上位机根本打不开那个设备节点。
 * 独立成厂商集合后，Windows 会创建一个可被 hidapi 自由打开的节点。
 */
const uint8_t hid_report_desc_vendor[] = {
    0x06, 0x00, 0xFF,                   /* Usage Page (Vendor Defined 0xFF00) */
    0x09, 0x01,                         /* Usage (0x01)                       */
    0xA1, 0x01,                         /* Collection (Application)  ← 只用一个集合，
                                           避免 Windows 把 Feature/Input 拆成两个设备节点 */

    /* ── 配置通道：Feature Report (RID 3)，主机发起 ── */
    0x85, KB_CFG_REPORT_ID,             /*   Report ID (3)                    */
    0x09, 0x01,                         /*   Usage (0x01)                     */
    0x15, 0x00,                         /*   Logical Minimum (0)              */
    0x26, 0xFF, 0x00,                   /*   Logical Maximum (255)            */
    0x75, 0x08,                         /*   Report Size (8)                  */
    0x95, KB_CFG_PAYLOAD_SIZE,          /*   Report Count (63)                */
    0xB2, 0x02, 0x00,                   /*   Feature (Data,Var,Abs)           */

    /* ── 实时上报：Input Report (RID 4)，设备主动 ──
     Report Count 用 KB_INPUT_PAYLOAD 符号而不是写死数字 ——
     改帧长时描述符会自动跟着变（这次从 32 涨到 52 就是这样）。
     TinyUSB 的 CFG_TUD_HID_EP_BUFSIZE 默认 64，52 装得下。 */
    0x85, KB_RID_INPUT,                 /*   Report ID (4)                    */
    0x09, 0x02,                         /*   Usage (0x02)                     */
    0x15, 0x00,                         /*   Logical Minimum (0)              */
    0x26, 0xFF, 0x00,                   /*   Logical Maximum (255)            */
    0x75, 0x08,                         /*   Report Size (8)                  */
    0x95, KB_INPUT_PAYLOAD,             /*   Report Count (52，见 app_config.h) */
    0x81, 0x02,                         /*   Input (Data,Var,Abs)             */

    0xC0                                /* End Collection                     */
};

/* ══════════════════════════════════════════════════════════════
 *  配置描述符（1 个配置 / 2 个接口）
 * ══════════════════════════════════════════════════════════════ */
#define CONFIG_TOTAL_LEN  (TUD_CONFIG_DESC_LEN + TUD_HID_DESC_LEN + TUD_HID_DESC_LEN)

const uint8_t desc_configuration[] = {
    /* 配置号=1, 接口数=2, 字符串索引=0(无), 总长, 属性=0x20(远程唤醒), 200mA */
    TUD_CONFIG_DESCRIPTOR(1, 2, 0, CONFIG_TOTAL_LEN, 0x20, 200),

    /* ── 接口 0 (MI_00)：HID 引导键盘 ──
       _boot_protocol = HID_ITF_PROTOCOL_KEYBOARD → SubClass=Boot(1), Protocol=Keyboard(1)
       接口字符串索引 = 0（不设，与真实设备一致）*/
    TUD_HID_DESCRIPTOR(KB_ITF_KEYBOARD,             /* 接口号               */
                       0,                           /* 接口字符串：无       */
                       HID_ITF_PROTOCOL_KEYBOARD,   /* 引导协议 = 键盘      */
                       sizeof(hid_report_desc_keyboard),
                       KB_EP_KEYBOARD_IN,           /* EP1 IN               */
                       KB_EP_KEYBOARD_SIZE,         /* 8 字节               */
                       KB_EP_INTERVAL_MS),          /* 1ms                  */

    /* ── 接口 1 (MI_01)：HID 厂商自定义（配置 + 调试上报）──
       _boot_protocol = HID_ITF_PROTOCOL_NONE → SubClass=0, Protocol=0 */
    TUD_HID_DESCRIPTOR(KB_ITF_CONFIG,               /* 接口号               */
                       0,                           /* 接口字符串：无       */
                       HID_ITF_PROTOCOL_NONE,
                       sizeof(hid_report_desc_vendor),
                       KB_EP_CONFIG_IN,             /* EP2 IN               */
                       KB_EP_CONFIG_SIZE,           /* 64 字节              */
                       KB_EP_INTERVAL_MS),
};

/* ══════════════════════════════════════════════════════════════
 *  字符串描述符
 *
 *  索引 0 : 语言 ID（必须是索引 0）
 *  索引 1 : iManufacturer = "Compx"                 ← 实测读自真实接收器
 *  索引 2 : iProduct      = "2.4G Wireless Receiver"
 *
 *  刻意【没有】序列号、也没有接口字符串（iInterface = 0）——
 *  与真实 2.4G 接收器逐项一致：
 *      hidapi 里两边都显示  manufacturer='Compx'  product='2.4G Wireless Receiver'
 * ══════════════════════════════════════════════════════════════ */
const char *desc_string_table[] = {
    (char[]){0x09, 0x04},       /* 0: 语言 ID = English (US) —— 非 UTF-16，按字节发 */
    KB_STR_MANUFACTURER,        /* 1: iManufacturer = "Compx" */
    KB_STR_PRODUCT,             /* 2: iProduct = "2.4G Wireless Receiver" */
};

const int desc_string_count = sizeof(desc_string_table) / sizeof(desc_string_table[0]);
