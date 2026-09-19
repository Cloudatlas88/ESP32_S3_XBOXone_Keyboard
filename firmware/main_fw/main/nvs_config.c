/**
 * @file nvs_config.c
 * @brief 配置存储：RAM 暂存/生效双缓冲 + NVS 双槽 + 延迟批量写入
 *
 * 设计要点（对应需求书 §4.5）
 *   1. 上位机写配置只进【暂存区】，不碰 NVS
 *   2. 只有收到 CFG_SAVE 才 commit 到【生效区】并标脏
 *   3. 独立任务按【60 秒频率限制】落到 NVS
 *   4. 落盘失败不阻塞：RAM 里继续用新配置，只回错误给上位机
 *   5. NVS 双槽轮换：永远写【非活动槽】，写成功后再切换 active 指针，
 *      所以任何时刻至少有一个完整可用的槽
 *   6. 加载失败 → 回退另一槽 → 都坏则出厂默认，绝不带着损坏配置运行
 */
#include <string.h>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "nvs.h"
#include "nvs_flash.h"

#include "kb_config.h"
#include "input_scan.h"
#include "nvs_config.h"

static const char *TAG = "nvs_cfg";

#define NVS_NAMESPACE   "kbcfg"
#define KEY_SLOT_A      "slot_a"
#define KEY_SLOT_B      "slot_b"
#define KEY_ACTIVE      "active"
#define KEY_SEQ         "seq"

static kb_config_t s_active;
static kb_config_t s_staging;

static portMUX_TYPE s_mux = portMUX_INITIALIZER_UNLOCKED;
static volatile uint32_t s_epoch        = 0;
static volatile bool     s_dirty        = false;
static volatile uint32_t s_save_fail    = 0;
static volatile uint8_t  s_active_slot  = 0;

/* ── 写入配额（防写入风暴，不是"延迟落盘"）──
 *
 * ⚠️ 这里推翻了一版错误的设计：最初按需求书字面写成「一分钟内最多写一次」，
 *    并把落盘推迟到间隔满足为止。结果是：上位机收到 ACK、用户以为已保存、
 *    此时断电 → 数据丢失。实测复现过。
 *
 * 正确的取舍：
 *   · 磨损根本不是瓶颈（设计文档 §9.4 算过：64KB NVS + 1.1KB/次 ≈ 530 万次 ≈ 1400 年）
 *   · 频率限制的真正价值是【防止上位机 bug 导致循环写入】
 *   · 所以：CFG_SAVE 立即落盘（100ms 内），只对【一分钟内超过 N 次】的异常情况拒绝
 */
#define NVS_SAVE_WINDOW_MS      60000
#define NVS_MAX_SAVES_PER_WINDOW 10

static uint32_t s_saves_in_window = 0;
static int64_t  s_window_start_us = 0;

/* ══════════════════════════════════════════════════════════════
 *  NVS 读写
 * ══════════════════════════════════════════════════════════════ */

/** 从指定槽读配置；成功返回 true */
/**
 * 把配置里的摇杆轴向参数下发给 input_scan。
 *
 * ★ 为什么要有这一步：0x0003 时死区/反向存在配置里，但 input_scan 用的是
 *   **编译期常量** STICK_DEADZONE_PCT —— 界面上调死区根本不生效（字段存了没人读）。
 *   0x0004 起由配置下发，配置一就绪就同步过去，改了立刻生效。
 */
static void apply_stick_params(const kb_config_t *cfg)
{
    for (int s = 0; s < STICK_COUNT; s++) {
        input_scan_set_stick_params(s,
                                    cfg->stick[s].deadzone_pct,
                                    cfg->stick[s].invert_x != 0,
                                    cfg->stick[s].invert_y != 0);
    }
}

static bool load_slot(nvs_handle_t h, const char *key, kb_config_t *out)
{
    size_t len = sizeof(*out);
    const esp_err_t err = nvs_get_blob(h, key, out, &len);
    if (err != ESP_OK) {
        ESP_LOGW(TAG, "读取槽 %s 失败: %s", key, esp_err_to_name(err));
        return false;
    }
    if (len != sizeof(*out)) {
        ESP_LOGW(TAG, "槽 %s 长度不符: %u != %u", key, (unsigned)len, (unsigned)sizeof(*out));
        return false;
    }
    if (!kb_config_validate(out)) {
        ESP_LOGW(TAG, "槽 %s 校验失败（magic/version/crc）", key);
        return false;
    }
    return true;
}

/**
 * 从 NVS 加载配置。
 * 顺序：活动槽 → 另一槽 → 出厂默认。任何一步成功就返回。
 */
static void load_from_nvs(void)
{
    nvs_handle_t h;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READONLY, &h);
    if (err != ESP_OK) {
        ESP_LOGW(TAG, "打开 NVS 命名空间失败(%s)，使用出厂默认", esp_err_to_name(err));
        kb_config_default(&s_active);
        return;
    }

    uint8_t active = 0;
    if (nvs_get_u8(h, KEY_ACTIVE, &active) != ESP_OK) active = 0;
    s_active_slot = active & 1;

    uint32_t seq = 0;
    nvs_get_u32(h, KEY_SEQ, &seq);

    const char *primary   = s_active_slot ? KEY_SLOT_B : KEY_SLOT_A;
    const char *secondary = s_active_slot ? KEY_SLOT_A : KEY_SLOT_B;

    if (load_slot(h, primary, &s_active)) {
        ESP_LOGI(TAG, "✔ 从活动槽 %s 加载配置（seq=%lu）", primary, (unsigned long)seq);
    } else if (load_slot(h, secondary, &s_active)) {
        ESP_LOGW(TAG, "活动槽损坏，已回退到备用槽 %s", secondary);
        s_active_slot ^= 1;
    } else {
        ESP_LOGE(TAG, "两个槽都不可用 —— 恢复出厂默认");
        kb_config_default(&s_active);
    }

    nvs_close(h);
    s_staging = s_active;
}

/** 把 s_active 写到【非活动槽】，成功后切换 active。返回 esp_err_t */
static esp_err_t save_to_nvs(void)
{
    nvs_handle_t h;
    esp_err_t err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &h);
    if (err != ESP_OK) return err;

    const uint8_t target = (uint8_t)(s_active_slot ^ 1);
    const char   *key    = target ? KEY_SLOT_B : KEY_SLOT_A;

    kb_config_t snapshot;
    portENTER_CRITICAL(&s_mux);
    snapshot = s_active;
    portEXIT_CRITICAL(&s_mux);
    kb_config_seal(&snapshot);          /* 落盘前重新封一下 CRC */

    err = nvs_set_blob(h, key, &snapshot, sizeof(snapshot));
    if (err == ESP_OK) err = nvs_set_u8(h, KEY_ACTIVE, target);
    if (err == ESP_OK) {
        uint32_t seq = 0;
        nvs_get_u32(h, KEY_SEQ, &seq);
        err = nvs_set_u32(h, KEY_SEQ, seq + 1);
    }
    if (err == ESP_OK) err = nvs_commit(h);      /* ★ 原子提交点 */

    nvs_close(h);

    if (err == ESP_OK) {
        s_active_slot = target;
        ESP_LOGI(TAG, "✔ 配置已写入槽 %s", key);
    } else {
        ESP_LOGE(TAG, "写 NVS 失败: %s", esp_err_to_name(err));
    }
    return err;
}

/* ══════════════════════════════════════════════════════════════
 *  延迟批量写入任务
 * ══════════════════════════════════════════════════════════════ */

static void nvs_task(void *arg)
{
    (void)arg;
    int retry = 0;

    while (1) {
        vTaskDelay(pdMS_TO_TICKS(100));

        if (!s_dirty) { retry = 0; continue; }

        /* ★ 脏了就【立刻落盘】（100ms 内完成）。
           不做"延迟到一分钟"那种会丢数据的设计 —— 用户点保存后随时可能断电。 */
        const esp_err_t err = save_to_nvs();

        if (err == ESP_OK) {
            s_dirty = false;
            retry   = 0;
            s_saves_in_window++;
        } else {
            s_save_fail++;
            /* ★ 写失败不阻塞系统：RAM 里继续用新配置工作，2 秒后重试，最多 3 次 */
            if (++retry >= 3) {
                ESP_LOGE(TAG, "连续 3 次写入失败，放弃本次保存（RAM 配置仍然生效）");
                s_dirty = false;
                retry   = 0;
            } else {
                vTaskDelay(pdMS_TO_TICKS(2000));
            }
        }
    }
}

/* ══════════════════════════════════════════════════════════════
 *  对外接口
 * ══════════════════════════════════════════════════════════════ */

esp_err_t nvs_config_init(void)
{
    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_LOGW(TAG, "NVS 需要重新初始化（%s）", esp_err_to_name(err));
        ESP_ERROR_CHECK(nvs_flash_erase());
        err = nvs_flash_init();
    }
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "nvs_flash_init 失败: %s", esp_err_to_name(err));
        kb_config_default(&s_active);
        s_staging = s_active;
        return err;
    }

    load_from_nvs();
    s_staging         = s_active;
    s_window_start_us = esp_timer_get_time();
    s_saves_in_window = 0;

    ESP_LOGI(TAG, "配置就绪: 死区 左%u%%/右%u%%  宏 %u 个",
             s_active.stick[STICK_L].deadzone_pct,
             s_active.stick[STICK_R].deadzone_pct,
             s_active.macro_count);

    /* ★ 把两个摇杆的轴向参数下发给 input_scan。
     *
     * 为什么必须做：0x0003 时死区/反向存在配置里，但 input_scan 用的是
     * **编译期常量** STICK_DEADZONE_PCT —— 界面上调死区根本不生效（字段读了没人用）。
     * 0x0004 起由配置下发，所以配置一就绪就要同步过去，
     * 否则扫描任务会用默认值跑，用户改了没反应。
     */
    apply_stick_params(&s_active);

    return ESP_OK;
}

esp_err_t nvs_config_start(void)
{
    if (xTaskCreatePinnedToCore(nvs_task, "nvs_cfg", 4096, NULL, 3, NULL, 1) != pdPASS) {
        ESP_LOGE(TAG, "创建 NVS 任务失败");
        return ESP_ERR_NO_MEM;
    }
    return ESP_OK;
}

const kb_config_t *nvs_config_active(void)  { return &s_active; }
kb_config_t       *nvs_config_staging(void) { return &s_staging; }
uint32_t           nvs_config_epoch(void)   { return s_epoch; }
bool               nvs_config_is_dirty(void){ return s_dirty; }
uint32_t           nvs_config_save_fail(void){ return s_save_fail; }

void nvs_config_snapshot(kb_config_t *out)
{
    portENTER_CRITICAL(&s_mux);
    *out = s_active;
    portEXIT_CRITICAL(&s_mux);
}

int64_t nvs_config_ms_until_save_allowed(void)
{
    /* 保留接口但语义变了：现在只有在【超过配额】时才返回非零，
       表示"本窗口内写入次数过多，请稍后再试"，而不是"还没到时间"。 */
    const int64_t now = esp_timer_get_time();
    if (now - s_window_start_us > (int64_t)NVS_SAVE_WINDOW_MS * 1000) {
        s_window_start_us = now;
        s_saves_in_window = 0;
        return 0;
    }
    return (s_saves_in_window >= NVS_MAX_SAVES_PER_WINDOW) ? 1000 : 0;
}

bool nvs_config_rate_ok(void)
{
    const int64_t now = esp_timer_get_time();
    if (now - s_window_start_us > (int64_t)NVS_SAVE_WINDOW_MS * 1000) {
        s_window_start_us = now;
        s_saves_in_window = 0;
    }
    return s_saves_in_window < NVS_MAX_SAVES_PER_WINDOW;
}

esp_err_t nvs_config_commit_staging(void)
{
    /* 上位机下发的暂存区必须自洽 */
    kb_config_t tmp;
    portENTER_CRITICAL(&s_mux);
    tmp = s_staging;
    portEXIT_CRITICAL(&s_mux);

    if (!kb_config_validate(&tmp)) {
        ESP_LOGE(TAG, "暂存区校验失败，拒绝提交");
        return ESP_ERR_INVALID_CRC;
    }

    portENTER_CRITICAL(&s_mux);
    s_active = tmp;
    s_epoch++;
    s_dirty  = true;
    portEXIT_CRITICAL(&s_mux);

    ESP_LOGI(TAG, "配置已生效（RAM），epoch=%lu，等待落盘", (unsigned long)s_epoch);
    return ESP_OK;
}

esp_err_t nvs_config_request_save(void){
    /* 只做配额检查：允许就立刻标脏，由 nvs 任务在 100ms 内落盘 */
    return nvs_config_rate_ok() ? ESP_OK : ESP_ERR_INVALID_STATE;
}

bool nvs_config_flush_now(void)
{
    /* 同步强制落盘 —— 给「软重启」命令用。
     *
     * 平时落盘是 nvs 任务异步做的（100ms 内完成）。重启前必须确认真的写完了，
     * 否则用户刚点的「保存」会随着重启丢掉，而他明明看到了"保存成功"。
     *
     * 直接在这里调用 save_to_nvs() 而不投递给 nvs 任务：
     *   · 调用者是 USB 回调上下文，本来就不该长时间阻塞，但一次 NVS 写入约 10~30ms，
     *     在软重启前这几十毫秒完全可以接受（反正马上就要重启了）
     *   · 走队列的话没法"等它写完"，而这里需要的正是"写完再返回" */
    if (!s_dirty) return true;

    const esp_err_t err = save_to_nvs();
    if (err == ESP_OK) {
        s_dirty = false;
        s_saves_in_window++;
        ESP_LOGI(TAG, "重启前已强制落盘");
        return true;
    }

    s_save_fail++;
    ESP_LOGE(TAG, "重启前强制落盘失败（%s）—— 本次改动可能丢失", esp_err_to_name(err));
    return false;
}

esp_err_t nvs_config_factory_reset(void)
{
    portENTER_CRITICAL(&s_mux);
    kb_config_default(&s_active);
    s_staging = s_active;
    s_epoch++;
    s_dirty = true;
    portEXIT_CRITICAL(&s_mux);

    ESP_LOGW(TAG, "已恢复出厂默认，epoch=%lu", (unsigned long)s_epoch);
    return ESP_OK;
}
