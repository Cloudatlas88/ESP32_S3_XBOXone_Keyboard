/**
 * @file macro_engine.h
 * @brief 宏引擎 —— 非阻塞状态机
 *
 * 对应需求书 §4.4：
 *   · 单键宏 / 组合键宏
 *   · 宏步骤可插入随机延迟
 *   · 非阻塞（禁止用 delay 计时，见下）
 *   · 总超时保护（默认 30 秒），超时强制终止并【释放所有按键】
 *
 * ⚠️ 关于「禁止 delay()」的正确理解：
 *   指的是【禁止用 delay 作为计时手段】，不是"永不调用延时函数"。
 *   状态机必须周期性让出 CPU，否则会饿死同核低优先级任务。
 *   本实现：用 esp_timer_get_time() 判断时间到没到，
 *           用 vTaskDelay(5ms) 只是让出 CPU。两者职责分离，
 *           所以调度抖动不会累积成延迟误差。
 */
#pragma once

#include <stdint.h>
#include <stdbool.h>
#include "esp_err.h"
#include "kb_config.h"

#ifdef __cplusplus
extern "C" {
#endif

/** 初始化（清空状态；需在 nvs_config_init 之后调用）*/
esp_err_t macro_engine_init(void);

/** 创建宏任务（Core 1）*/
esp_err_t macro_engine_start(void);

/** 触发一个宏（非阻塞，立即返回）—— 每次都从头启动 */
esp_err_t macro_trigger(uint8_t macro_id);

/**
 * ★ 触发按钮 / 上位机「测试触发」统一入口。
 *
 *   · 循环宏（macro flags bit0 = 1）——【开关式】：
 *       正在跑的正是它 → 停止；否则 → 启动。
 *       也就是"按一下开、再按一下停"。
 *   · 非循环宏 ——【触发式】：每次都从头启动（正在跑就重来）。
 *
 * @param stopped  输出：true = 本次调用是【停止】，false = 是【启动/触发】。
 *                 可以为 NULL。
 *
 *                 ★ 这个输出参数是必须的：宏是"先入队、由宏任务稍后真正启动"的，
 *                   调用者【不能】用"这一刻 macro_is_busy()"去猜本次是开还是停 ——
 *                   刚触发时宏还没跑起来，is_busy() 仍是 false，
 *                   界面就会把"刚启动"显示成"已停止"（实测踩过）。
 *                   判断必须基于本次到底做了什么。
 */
esp_err_t macro_toggle_or_trigger(uint8_t macro_id, bool *stopped);

/** 中止当前宏（会释放所有由宏按下的键）*/
void macro_abort(void);

/** 配置变更后调用：中止当前宏并重载定义 */
void macro_reload(void);

/* ── 状态查询（诊断）── */
bool     macro_is_busy(void);
uint8_t  macro_current(void);      /* 0xFF = 空闲 */
uint32_t macro_run_count(void);
uint32_t macro_abort_count(void);  /* 超时/中止次数 */
uint32_t macro_step_count(void);   /* 累计执行步数 */

#ifdef __cplusplus
}
#endif
