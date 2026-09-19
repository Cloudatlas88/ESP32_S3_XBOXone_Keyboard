/**
 * @file keymap.h
 * @brief 把输入状态按配置映射成键盘按键
 */
#pragma once

#include "input_scan.h"
#include "kb_config.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * 映射源数量 = 槽位数量（24）
 *
 * ★ 0x0004 起**所有映射源统一成"槽位"**：
 *   十字键 4 个、两个摇杆各 4 个、单键 12 个，全是同一个数组里的下标。
 *   0x0003 时是「4 个方向源 + 5 个按钮源」两套，还要在 keymap 里再做一次
 *   方向反向、死区判定 —— 现在那些都归 input_scan（它才知道摇杆的实际状态），
 *   keymap 只干一件事：**槽位按下 → 发对应键**。
 */
#define KB_SRC_COUNT  SLOT_COUNT

/** 释放全部映射源（配置切换/故障恢复时调用，防止卡键）*/
void keymap_reset(void);

/**
 * 按当前输入状态与配置驱动 hid_keyboard。
 * 内部做增量化：只 press 新按下的、只 release 已松开的。
 */
void keymap_apply(const input_state_t *st, const kb_config_t *cfg);

/** 当前处于按下状态的映射源数量（诊断）*/
int keymap_active_count(void);

#ifdef __cplusplus
}
#endif
