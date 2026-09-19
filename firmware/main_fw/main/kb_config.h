/**
 * @file kb_config.h
 * @brief 设备配置数据结构 —— 固件与上位机共享的字节布局
 *
 * ⚠️ 这个结构体在固件（ESP32）和上位机（C#/Python）两边必须【逐字节一致】。
 *    改动时两边都要改，并同步 KB_CFG_VERSION（上位机会校验，版本不符直接拒绝）。
 *
 * 版本历史：
 *   0x0001  初始版本（摇杆 + 按钮映射）
 *   0x0002  增加宏定义区
 *   0x0003  随机延迟改为【基准值 + 抖动%】；新增宏总开关按键与上电默认状态
 *           （★ 后续移除了宏总开关功能：偏移 57/58 那两字节改为保留。
 *             因为固件已完全不读它们，任何旧值都能正常加载，
 *             所以【不需要】再升版本 —— 升版本会让用户白白丢一次配置。）
 *   0x0004  ★★ Xbox 手柄改造：按钮 5 → 24 个槽位，摇杆 1 → 2 个（各自独立
 *           死区/反向/四方向映射）。旧配置判为无效并回落到出厂默认 ——
 *           这次是换硬件，属于预期行为。
 *
 * 尺寸核算（0x0004）：
 *   头部 4×uint16          8
 *   stick[2] × 8          16    ★ 每个摇杆各有独立死区/反向/四方向
 *   buttons[24] × 8      192    (keycode/modifiers/trigger/flags/macro_id + 3 保留)
 *   macro_count + 3        4
 *   macros[4] × 68       272    (4 头 + 8 步 × 8 字节)  ★ 宏布局未动
 *   reserved              64
 *   ─────────────────────────
 *   合计                 556 字节
 *
 *   单包负载 54 字节 → 556 / 54 = 10.3 → 需 11 包传输
 *
 * ★ 契约文档：项目结构.md §六（配置格式与上报帧布局）
 */
#pragma once

#include <stdint.h>
#include <stdbool.h>
#include "app_config.h"

#define KB_CFG_MAGIC     0x4B42u        /* "KB"（uint16_t 字段，只能用 16 位值）*/
#define KB_CFG_VERSION   0x0006         /* ★ 宏步数 8 → 20 */

#define MACRO_MAX        4              /* 最多 4 个宏 */
#define MACRO_STEP_MAX   20             /* 每个宏最多 20 步（2026-09-18 从 8 提升）*/

/* 按钮 flags */
#define KB_BTNFLAG_ENABLED   0x01
#define KB_BTNFLAG_HAS_MACRO 0x02       /* 该按钮触发宏而不是单键 */

/* ⚠️ 原来的「按钮触发方式」概念（按下/释放/长按）已移除。
 *
 * 实体键盘就只有一种逻辑：**按下 → 键按下（一直保持，主机自己重复）；松开 → 键抬起**。
 * 所以"触发方式"这个可选项本身就是多余的，不需要配。
 * kb_button_cfg_t.trigger 字段保留占位（配置布局不变），固件不再读取它 ——
 * 老配置里存着旧值也能正常加载。 */

/* 宏步骤动作 */
#define MACRO_ACT_KEY_TAP     0         /* 单键敲击：按下+短暂保持+释放 */
#define MACRO_ACT_COMBO       1         /* 组合键：modifiers + keycode */
#define MACRO_ACT_KEY_DOWN    2         /* 按住不放 */
#define MACRO_ACT_KEY_UP      3         /* 释放 */
#define MACRO_ACT_DELAY       4         /* 固定延迟 delay_min_ms */
#define MACRO_ACT_RAND_DELAY  5         /* 随机延迟：delay_min_ms = 基准值(ms)
                                                          delay_max_ms = 抖动(%) 0~90 */

#define MACRO_MAX_JITTER_PCT  90        /* 抖动上限，再大分布就退化了 */

#define KB_MACRO_SW_NONE      0xFF      /* 已废弃：总开关功能已移除，保留常量免得老代码编译不过 */

/* 实时状态字节（Input Report 偏移 31） */
#define KB_STAT_MACRO_BUSY    0x02      /* 有宏正在执行 */

/* 上位机触发宏时 DATA[0] 的取值 */
#define KB_MACRO_RUN_ABORT    0xFF      /* 中止当前宏 */

/** 摇杆配置 —— ★ 只放**轴向行为**，不放键映射
 *
 *  ★ 设计要点（0x0004 定的）：**24 个槽位的键映射全部在 buttons[] 里**，
 *    包括两个摇杆的 8 个方向槽位（16-23）。
 *
 *    0x0003 里方向键是放在 stick.up_key/down_key/... 的，
 *    如果 0x0004 同时保留 stick 里的方向键**和** buttons[16..23]，
 *    同一份映射就有两处可写 —— 改一处另一处还是旧值，必然出岔子，
 *    而且"哪个生效"要靠读代码才知道。
 *    所以这里只留轴向行为，键映射统一走 buttons[]。
 */
typedef struct __attribute__((packed)) {
    uint8_t deadzone_pct;   /* 死区百分比 0 ~ 60 */
    uint8_t invert_x;       /* 0/1 */
    uint8_t invert_y;       /* 0/1 */
    uint8_t reserved[5];    /* 保留（原来是 up/down/left/right 四个键码）*/
} kb_stick_cfg_t;

/** 单个按钮映射 */
typedef struct __attribute__((packed)) {
    uint8_t keycode;        /* 目标 HID keycode，0 = 不映射 */
    uint8_t modifiers;      /* MOD_* 位掩码（组合键用）*/
    uint8_t trigger;        /* ⚠️ 已废弃（原"按下/释放/长按"）。保留占位，固件不读 */
    uint8_t flags;          /* KB_BTNFLAG_* */
    uint8_t macro_id;       /* 0xFF = 不使用宏；否则 MACRO_MAX 以内的索引 */
    uint8_t reserved[3];
} kb_button_cfg_t;

/** 宏的一步 */
typedef struct __attribute__((packed)) {
    uint8_t  action;        /* MACRO_ACT_* */
    uint8_t  keycode;       /* HID keycode，0 = 无 */
    uint8_t  modifiers;     /* MOD_* 位掩码 */
    uint8_t  reserved;
    uint16_t delay_min_ms;
    uint16_t delay_max_ms;
} macro_step_t;

/** 一个宏 */
typedef struct __attribute__((packed)) {
    uint8_t  step_count;        /* 有效步数 */
    uint8_t  flags;             /* bit0 = 循环执行
                                   ★ 勾了循环：触发按钮按一下开始、再按一下停止 */
    uint16_t total_timeout_ms;  /* 0 = 用 MACRO_TIMEOUT_MS */
    macro_step_t steps[MACRO_STEP_MAX];
} macro_def_t;

/** 宏 flags */
#define KB_MACRO_FLAG_LOOP    0x01  /* 循环执行（触发按钮变成开/关切换）*/

/** 完整配置（NVS blob，也是 Feature Report 传输的载荷）*/
typedef struct __attribute__((packed)) {
    uint16_t magic;         /* KB_CFG_MAGIC */
    uint16_t version;       /* KB_CFG_VERSION */
    uint16_t size;          /* sizeof(kb_config_t) */
    uint16_t crc16;         /* 覆盖【偏移 8 之后】的全部字节 */

    kb_stick_cfg_t  stick[STICK_COUNT];   /* ★ [0]=左摇杆 [1]=右摇杆 */
    kb_button_cfg_t buttons[SLOT_COUNT];  /* ★ 24 个槽位，下标即 slot_idx_t */

    uint8_t  macro_count;
    uint8_t  reserved2[3];       /* 原「宏总开关按键 / 上电默认启用」，
                                    功能已移除，保留字节维持布局稳定 */

    macro_def_t macros[MACRO_MAX];

    uint8_t reserved[64];   /* 后续扩展 */
} kb_config_t;

_Static_assert(sizeof(kb_stick_cfg_t)  == 8,    "摇杆配置项应为 8 字节");
_Static_assert(sizeof(kb_button_cfg_t) == 8,    "按钮配置项应为 8 字节");
_Static_assert(sizeof(kb_config_t)     == 956,  "kb_config_t 尺寸变了 —— 上位机也要同步改");
_Static_assert(STICK_COUNT == 2,                "这份配置按 2 个摇杆算尺寸");
_Static_assert(SLOT_COUNT  == 26,               "这份配置按 26 个槽位算尺寸");
_Static_assert(MACRO_STEP_MAX == 20,            "这份配置按每宏 20 步算尺寸");

/** 填出厂默认值 */
void kb_config_default(kb_config_t *cfg);

/** 计算并写入 crc16 字段 */
void kb_config_seal(kb_config_t *cfg);

/** 校验 magic / version / size / crc16；返回 true 表示可用 */
bool kb_config_validate(const kb_config_t *cfg);

/** 返回校验失败的简短原因（便于日志定位）*/
const char *kb_config_validate_reason(const kb_config_t *cfg);
