/**
 * @file hid_keyboard.h
 * @brief HID 键盘报告的【单点出口】
 *
 * ⚠️ 所有按键来源（摇杆映射、按钮映射、宏、故障保护）都必须经过这里，
 *    【禁止】任何模块直接调用 tud_hid_n_keyboard_report()。
 *    否则多来源会互相覆盖报告 —— 这是本项目最容易出的 bug。
 *
 * 关键机制：引用计数
 *   同一个键码可能被「手动映射」和「宏」同时按下。若用简单集合，
 *   先释放的一方会把另一方还需要的键松掉。引用计数保证
 *    「最后一个释放者才真正发释放报告」。
 *
 *   ★ 修饰键（Ctrl/Shift/Alt/GUI）**也各自有引用计数** ——
 *     这一点是踩过坑才补上的：原来只有键码计数，修饰键是简单的
 *     `s_modifiers |= modifiers`，而 release 时完全不动它，
 *     结果映射成 Ctrl+A 的按钮按一下再松开，「左Ctrl 会永久卡住」，
 *     之后所有按键都变成 Ctrl+某键。详见 hid_kb_release 的说明。
 *
 * 6 键上限：
 *   标准键盘报告只有 6 个键码槽。超过时按 HID 规范填 ErrorRollOver（0x01 × 6）。
 */
#pragma once

#include <stdint.h>
#include <stdbool.h>
#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

#define KB_KEYSET_SLOTS   6      /* 标准键盘报告槽位数 */

esp_err_t hid_kb_init(void);

/* ── 按键动作（线程安全）── */

/**
 * 按下一个键（可带修饰键）。修饰键的对应位引用计数 +1。
 *
 * 多次 press 同一个键码会累加引用计数，只有等到同样次数的 release
 * 才真正抬起来 —— 这样"两个来源都要这个键"时不会互相踩。
 */
bool hid_kb_press(uint8_t keycode, uint8_t modifiers);

/**
 * 释放一个键。
 *
 * ★ <paramref name="modifiers"/> 必须传【按下时用的那一份】——
 *   修饰键引用计数要按同样的位减回去，减到 0 才真正抬起来。
 *
 *   为什么必须这样：实体键盘松开 Ctrl 时，报告里的 Ctrl 位就清掉了。
 *   早先的签名是 release(keycode)，拿不到修饰键，
 *   于是 `s_modifiers` 只增不减 —— 按下 Ctrl+A 再松开，
 *   主机就会认为 Ctrl 一直按着（实测确认的卡键 Bug）。
 */
bool hid_kb_release(uint8_t keycode, uint8_t modifiers);

/** 强制清空全部按键与修饰键（不关心引用计数）*/
void hid_kb_release_all(void);

/* ── 状态 ── */
bool     hid_kb_is_mounted(void);
uint8_t  hid_kb_get_leds(void);
uint32_t hid_kb_overflow_count(void);
uint32_t hid_kb_report_count(void);
uint8_t  hid_kb_pressed_count(void);

/**
 * 当前报告里的修饰键字节。
 * 给上位机做诊断用 —— 靠它才能验证"松开后修饰键有没有真的清掉"。
 */
uint8_t  hid_kb_modifiers(void);

/* ── 由 USB 事件驱动 ── */
void hid_kb_on_mount(void);       /* 枚举完成后：清空并先发全零报告 */
void hid_kb_on_umount(void);      /* 断开/挂起：清空内部状态（断线时发不出去）*/
void hid_kb_on_led_report(uint8_t led_byte);

/** 把当前 keyset 真正发一次（由 hid_tx 任务按周期调用）*/
void hid_kb_flush(void);

#ifdef __cplusplus
}
#endif
