/**
 * @file nvs_config.h
 * @brief 配置存储：RAM 双缓冲 + NVS 双槽 + 60 秒延迟批量写入
 *
 * 两个缓冲区：
 *   active  —— 当前生效（扫描/映射任务读它）
 *   staging —— 上位机写入目标（不影响当前工作）
 *
 * 提交路径：
 *   上位机 CFG_WRITE → 写 staging（可多包）
 *   上位机 CFG_SAVE  → nvs_config_commit_staging()：staging → active（纯 RAM，立即生效）
 *                      → 标脏，nvs 任务在 100ms 内【立即落盘】
 *
 * ⚠️ 这里刻意【不做】"延迟到一分钟才落盘"：那样用户点了保存、收到成功、
 *    此时断电就会丢数据（实测复现过）。磨损不是瓶颈（设计文档 §9.4 估算约 1400 年），
 *    频率限制只用于拦截"一分钟内超过 N 次"的异常写入风暴。
 */
#pragma once

#include "esp_err.h"
#include "kb_config.h"

#ifdef __cplusplus
extern "C" {
#endif

/** 一分钟窗口内允许的最大落盘次数（防写入风暴，非磨损保护）*/
#define NVS_MAX_SAVES_PER_MIN   10

/** 初始化 NVS 并加载配置（活动槽 → 备用槽 → 出厂默认）*/
esp_err_t nvs_config_init(void);

/** 创建延迟写入任务（Core 1）*/
esp_err_t nvs_config_start(void);

/** 当前生效配置（只读；内容可能被 commit 直接替换，取用请尽快）*/
const kb_config_t *nvs_config_active(void);

/** 取一份生效配置的快照（临界区整体拷贝，供映射任务安全使用）*/
void nvs_config_snapshot(kb_config_t *out);

/** 暂存区（上位机写入目标）*/
kb_config_t *nvs_config_staging(void);

/** 每次 commit +1，消费者据此判断是否要重载映射 */
uint32_t nvs_config_epoch(void);

/** staging → active（RAM 立即生效）并标脏等待落盘 */
esp_err_t nvs_config_commit_staging(void);

/** 恢复出厂默认并请求落盘 */
esp_err_t nvs_config_factory_reset(void);

/** 是否还有未落盘的改动 */
bool nvs_config_is_dirty(void);

/**
 * 同步强制落盘（不等 nvs 任务，写完才返回）。
 * 给「软重启」用：确保重启前刚保存的配置真的进了 NVS。
 * @return true = 已落盘或本来就无需落盘；false = 写失败（改动可能丢失）
 */
bool nvs_config_flush_now(void);

/** 写入配额检查（滑动 60 秒窗口）。true = 允许落盘 */
bool nvs_config_rate_ok(void);

/** 距离配额恢复还有多少毫秒（0 = 现在就能写）。仅用于给上位机提示 */
int64_t nvs_config_ms_until_save_allowed(void);

/** 累计落盘失败次数 */
uint32_t nvs_config_save_fail(void);

#ifdef __cplusplus
}
#endif
