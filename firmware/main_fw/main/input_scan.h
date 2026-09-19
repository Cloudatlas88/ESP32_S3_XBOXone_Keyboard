/**
 * @file input_scan.h
 * @brief 16 路数字输入 + 2 个摇杆（4 路 ADC）扫描（1ms 周期）
 *
 * ★★ 24 槽位模型（0x0004）
 *
 *   0x0003 时输入分两套：4 个"方向源" + 5 个"按钮源"，各自有独立数组。
 *   0x0004 起**统一成 24 个槽位**（slot_idx_t）：
 *     0-11   单键（A B X Y LB RB View Menu Xbox Share L3 R3）—— 数字 GPIO
 *     12-15  十字键 ↑ ↓ ← →                                —— 数字 GPIO
 *     16-19  左摇杆 ↑ ↓ ← →                                —— ADC 派生
 *     20-23  右摇杆 ↑ ↓ ← →                                —— ADC 派生
 *     24-25  扳机 LT / RT                                  —— 数字 GPIO（阈值式）
 *   keymap 那边因此只需要一个 24 位的位图，不再分两套。
 *
 * 引脚见 app_config.h（按实际焊接），权威数据 hardware_ref/xbox_pcb/pinmap.csv。
 */
#pragma once

#include <stdint.h>
#include <stdbool.h>
#include "esp_err.h"
#include "app_config.h"

/* 状态标志 */
#define INP_FLAG_VALID        0x01   /* 数据已就绪 */
#define INP_FLAG_MOUNTED      0x02   /* USB 已挂载 */
#define INP_FLAG_STICK_FAULT  0x04   /* 有摇杆轴贴轨（短路/断路），已禁用该轴 */

/* ── 4 个轴的编号（raw/center/tmin/tmax/pct 数组下标）── */
#define AXIS_LX   0
#define AXIS_LY   1
#define AXIS_RX   2
#define AXIS_RY   3
#define AXIS_COUNT  4

/* ── 方向位图的槽位基址（用于把槽位位图翻译成方向位）── */
#define DIRBASE_DPAD    SLOT_DPAD_UP   /* 12 */
#define DIRBASE_LSTICK  SLOT_LS_UP     /* 16 */
#define DIRBASE_RSTICK  SLOT_RS_UP     /* 20 */

/* 数字输入个数（= 24 槽位里由 GPIO 直接驱动的那些）*/
#define DIGITAL_INPUT_COUNT  18

/**
 * 输入状态快照。
 *
 * 注意：这是**逻辑状态**，不是线上字节布局 —— 打包成 Input Report 由 main.c 负责
 *       （偏移表在 app_config.h 的 IN_OFF_*）。
 */
typedef struct {
    uint8_t  flags;                    /* INP_FLAG_* */
    uint32_t slots;                    /* ★ bit0..23 = 槽位 0..23（已防抖）*/
    uint8_t  btn_raw[4];               /* ★ 24 个槽位的原始 GPIO 电平（未防抖，接线诊断）*/
    int8_t   pct[AXIS_COUNT];          /* 4 轴归一化 -100..+100（超程按 100 截断）*/
    uint16_t raw[AXIS_COUNT];          /* 4 轴 ADC 原始值 0..4095 */
    uint16_t center[AXIS_COUNT];       /* 4 轴开机自校准中心 */
    uint16_t tmin[AXIS_COUNT];         /* ★ 自学习行程：负方向极值 */
    uint16_t tmax[AXIS_COUNT];         /* ★ 自学习行程：正方向极值 */
    uint8_t  axis_fault;               /* bit0..3 = LX/LY/RX/RY 故障 */
    uint32_t uptime_ms;
    uint32_t seq;
} input_state_t;

/** 把槽位位图里某 4 个连续槽位翻译成方向位（bit0 上 / bit1 下 / bit2 左 / bit3 右）*/
static inline uint8_t slots_to_dirs(uint32_t slots, int base_slot)
{
    uint8_t d = 0;
    if (slots & (1u << (base_slot + 0))) d |= DIRBIT_UP;
    if (slots & (1u << (base_slot + 1))) d |= DIRBIT_DOWN;
    if (slots & (1u << (base_slot + 2))) d |= DIRBIT_LEFT;
    if (slots & (1u << (base_slot + 3))) d |= DIRBIT_RIGHT;
    return d;
}

/** 初始化 ADC 与 16 路数字输入，并对两个摇杆做中心自校准（约 0.5 秒）*/
esp_err_t input_scan_init(void);

/** 创建扫描任务（1ms 周期，Core 0）*/
esp_err_t input_scan_start(void);

/** 取状态快照（临界区保护，整体拷贝）*/
void input_scan_get_state(input_state_t *out);

/** 设置 USB 挂载标志（供 USB 事件回调调用）*/
void input_scan_set_mounted(bool mounted);

/**
 * 请求重置行程校准（线程安全，可在任意任务/回调里调用）。
 *
 * 注意语义：只置一个标志，真正的校准由扫描任务执行 ——
 * 因为校准要连续读几百次 ADC，绝不能和扫描任务并发访问同一 ADC 单元。
 */
void input_scan_request_calibration(void);

/** 累计 ADC 读失败次数（诊断用；读失败时会沿用上次有效值，不会填 0）*/
void input_scan_get_adc_errors(uint32_t out[AXIS_COUNT]);

/**
 * 下发某个摇杆的轴向参数（死区 / X、Y 反向）。
 *
 * ★ 为什么要有这个接口：0x0003 里死区是**编译期常量** `STICK_DEADZONE_PCT`，
 *   配置里的 `deadzone_pct` 字段虽然存了、上位机也能改，
 *   但固件**从来没人读它** —— 界面上调死区根本不生效。
 *   0x0004 起每个摇杆各有独立死区/反向，由配置下发到这里。
 *
 * 可在任意任务上下文调用（只写几个字节，扫描任务下一周期就会用上新值）。
 */
void input_scan_set_stick_params(int stick, uint8_t deadzone_pct,
                                 bool invert_x, bool invert_y);
