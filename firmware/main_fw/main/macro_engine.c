/**
 * @file macro_engine.c
 * @brief 宏引擎实现（非阻塞状态机 + 总超时保护 + 随机延迟）
 */
#include <string.h>
#include <math.h>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/queue.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "esp_random.h"

#include "app_config.h"
#include "hid_keyboard.h"
#include "macro_engine.h"
#include "nvs_config.h"

static const char *TAG = "macro";

/* ── 状态机 ── */
typedef enum {
    MS_IDLE = 0,
    MS_CHECK,
    MS_STEP_EXEC,
    MS_WAIT_DELAY,
    MS_FINISH,
    MS_ABORT,
} macro_state_t;

static volatile macro_state_t s_state = MS_IDLE;
static uint8_t  s_macro_id;         /* 当前宏索引 */
static uint8_t  s_step;             /* 当前步 */
static int64_t  s_start_us;         /* 起始时刻 */
static int64_t  s_deadline_us;      /* 当前延迟的截止时刻 */
static int64_t  s_timeout_us;       /* 本次宏的总超时 */

static const macro_def_t *s_def;    /* 指向当前宏定义（来自快照）*/
static macro_def_t s_def_copy;      /* 宏定义快照（配置可能被上位机改）*/

/* ── 宏按下的键（结束时必须全部释放）──
   ★ 连修饰键一起记：释放时要把它按下的 Ctrl/Shift/Alt 按引用计数减回去，
     否则组合键步骤跑完之后修饰键会永久卡住（实测确认过的 Bug）。 */
#define MACRO_HELD_MAX  8
typedef struct { uint8_t key; uint8_t mod; } macro_held_t;

static macro_held_t s_held_key[MACRO_HELD_MAX];
static uint8_t s_held_n;

/* 已按下、等延迟结束后要释放的键（KEY_TAP / COMBO 用）*/
static uint8_t s_pending_key;
static uint8_t s_pending_mod;
static bool    s_pending;

static QueueHandle_t s_req_q;

/* 执行中又来了新请求：先中止当前，等释放干净后再启动新的 */
static uint8_t s_next_req;
static bool    s_has_next;

static volatile uint32_t s_run_cnt;
static volatile uint32_t s_abort_cnt;
static volatile uint32_t s_step_cnt;

/* ══════════════════════════════════════════════════════════════
 *  随机延迟
 * ══════════════════════════════════════════════════════════════ */

/** 均匀分布 [lo, hi] */
static uint32_t rand_between(uint32_t lo, uint32_t hi)
{
    if (hi <= lo) return lo;
    return lo + (esp_random() % (hi - lo + 1));
}

/**
 * 正态分布随机延迟 —— 参数直接来自「基准值 + 抖动百分比」
 *
 *   基准值 base   = step.delay_min_ms   （用户界面上填的那个 ms）
 *   抖动   jitter = step.delay_max_ms   （百分比 0~90）
 *
 *   jitter% = 0        → 退化成固定延迟，正好等于 base
 *   jitter% = 30, base = 200ms
 *                      → 分布中心 200ms，范围 [140, 260]ms
 *
 * 为什么用正态分布而不是均匀分布：
 *   均匀分布每个值概率相同，看起来"太整齐"，机械感明显；
 *   真实人手敲击间隔是中间密、两头疏的，正态分布更接近。
 *   sigma 取抖动幅度的一半，这样 ±抖动 覆盖约 95% 的样本，
 *   极小概率的越界值截断在边界上。
 *
 * 随机源用 esp_random()：ESP32-S3 上是【硬件真随机数发生器】，
 * 由射频噪声与内建熵源驱动，不存在也不需要"种子"，
 * 比 srand()/rand() 那种周期可预测的伪随机数强得多。
 */
static uint32_t rand_gauss_base(uint32_t base_ms, uint32_t jitter_pct)
{
    if (base_ms == 0) base_ms = MACRO_RAND_DEFAULT_BASE_MS;
    if (jitter_pct > MACRO_MAX_JITTER_PCT) jitter_pct = MACRO_MAX_JITTER_PCT;

    if (jitter_pct == 0) return base_ms;        /* 抖动 0 = 固定延迟 */

    const uint32_t spread = (base_ms * jitter_pct) / 100u;
    if (spread == 0) return base_ms;

    const uint32_t lo = (base_ms > spread) ? (base_ms - spread) : 1u;
    const uint32_t hi = base_ms + spread;

    const double mean  = (double)base_ms;
    const double sigma = (double)spread / 2.0;

    /* Box-Muller：把两个均匀随机数转成一个标准正态分布样本 */
    const double u1 = ((double)esp_random() + 1.0) / 4294967297.0;   /* (0,1) */
    const double u2 = ((double)esp_random() + 1.0) / 4294967297.0;
    const double z  = sqrt(-2.0 * log(u1)) * cos(2.0 * M_PI * u2);

    int32_t v = (int32_t)(mean + z * sigma);
    if (v < (int32_t)lo) v = (int32_t)lo;
    if (v > (int32_t)hi) v = (int32_t)hi;
    return (uint32_t)v;
}

/* ══════════════════════════════════════════════════════════════
 *  按键记账
 * ══════════════════════════════════════════════════════════════ */

static void held_add(uint8_t key, uint8_t mod)
{
    if (key == 0) return;
    for (int i = 0; i < s_held_n; i++) if (s_held_key[i].key == key) return;
    if (s_held_n < MACRO_HELD_MAX) {
        s_held_key[s_held_n].key = key;
        s_held_key[s_held_n].mod = mod;
        s_held_n++;
    }
}

/** 移除并返回按下时用的修饰键（0 表示没记到）*/
static uint8_t held_remove(uint8_t key)
{
    for (int i = 0; i < s_held_n; i++) {
        if (s_held_key[i].key == key) {
            const uint8_t mod = s_held_key[i].mod;
            s_held_key[i] = s_held_key[--s_held_n];
            return mod;
        }
    }
    return 0;
}

/** 释放宏按下过的【全部】键 —— 正常结束和异常中止都必须走这里 */
static void release_everything(void)
{
    if (s_pending) {
        hid_kb_release(s_pending_key, s_pending_mod);
        s_pending = false;
    }
    for (int i = 0; i < s_held_n; i++) {
        hid_kb_release(s_held_key[i].key, s_held_key[i].mod);
    }
    s_held_n = 0;
}

/* ══════════════════════════════════════════════════════════════
 *  状态机
 * ══════════════════════════════════════════════════════════════ */

/** 执行一步，返回还需要等待的毫秒数（0 = 不用等，直接下一步）*/
static uint32_t exec_step(const macro_step_t *st)
{
    s_step_cnt++;

    switch (st->action) {
    case MACRO_ACT_KEY_TAP:
    case MACRO_ACT_COMBO: {
        /* 补一次释放：防止上一步的 pending 还没放 */
        if (s_pending) { hid_kb_release(s_pending_key, s_pending_mod); s_pending = false; }

        hid_kb_press(st->keycode, st->modifiers);
        s_pending_key = st->keycode;
        s_pending_mod = st->modifiers;
        s_pending     = true;

        /* 敲击保持时间：太短主机可能采样不到，太长会显得"粘" */
        return rand_between(MACRO_TAP_HOLD_MIN_MS, MACRO_TAP_HOLD_MAX_MS);
    }

    case MACRO_ACT_KEY_DOWN:
        hid_kb_press(st->keycode, st->modifiers);
        held_add(st->keycode, st->modifiers);
        return 0;

    case MACRO_ACT_KEY_UP: {
        /* ★ 用【记录下来的】修饰键去释放，而不是用这一步里填的 ——
           用户很可能在"按住"那步填了 Ctrl、在"释放"那步没填，
           用步骤里填的会减不掉修饰键引用计数，Ctrl 就卡住了。 */
        const uint8_t rec = held_remove(st->keycode);
        hid_kb_release(st->keycode, (uint8_t)(rec | st->modifiers));
        if (s_pending && s_pending_key == st->keycode) s_pending = false;
        return 0;
    }

    case MACRO_ACT_DELAY:
        return st->delay_min_ms ? st->delay_min_ms : 1;

    case MACRO_ACT_RAND_DELAY:
        /* delay_min_ms = 基准值(ms)，delay_max_ms = 抖动(%) */
        return rand_gauss_base(st->delay_min_ms, st->delay_max_ms);

    default:
        ESP_LOGW(TAG, "未知宏动作 %u，跳过", st->action);
        return 0;
    }
}

static void start_macro(uint8_t id)
{
    const kb_config_t *cfg = nvs_config_active();

    if (id >= MACRO_MAX || id >= cfg->macro_count) {
        ESP_LOGW(TAG, "宏 %u 不存在", id);
        s_state = MS_IDLE;
        return;
    }

    /* 拷贝一份定义：宏执行期间上位机可能改配置 */
    s_def_copy = cfg->macros[id];
    s_def      = &s_def_copy;

    if (s_def->step_count == 0 || s_def->step_count > MACRO_STEP_MAX) {
        ESP_LOGW(TAG, "宏 %u 步数非法 (%u)", id, s_def->step_count);
        s_state = MS_IDLE;
        return;
    }

    s_macro_id   = id;
    s_step       = 0;
    s_start_us   = esp_timer_get_time();
    s_timeout_us = (int64_t)(s_def->total_timeout_ms ? s_def->total_timeout_ms
                                                     : MACRO_TIMEOUT_MS) * 1000;
    s_pending    = false;
    s_held_n     = 0;
    s_run_cnt++;
    s_state      = MS_CHECK;

    ESP_LOGI(TAG, "▶ 宏 %u 开始（%u 步，超时 %lld ms）",
             id, s_def->step_count, (long long)(s_timeout_us / 1000));
}

static void macro_task(void *arg)
{
    (void)arg;
    uint8_t req;

    while (1) {
        /* ── 处理新请求（每轮都查，非阻塞）──
           用 xQueueReceive(...,0) 而不是在 IDLE 里阻塞等待，
           这样执行中也能立刻响应"换一个宏"的请求。 */
        if (xQueueReceive(s_req_q, &req, 0) == pdTRUE) {
            if (s_state != MS_IDLE) {
                s_next_req = req;
                s_has_next = true;
                s_state    = MS_ABORT;      /* 先干净地中止当前 */
            } else {
                start_macro(req);
            }
        }

        switch (s_state) {
        case MS_IDLE:
            /* 无需在这里等队列：循环顶端已经非阻塞查过了。
               这个分支只负责让出 CPU。 */
            break;

        case MS_CHECK:
            if (s_step >= s_def->step_count) {
                s_state = (s_def->flags & KB_MACRO_FLAG_LOOP) ? MS_STEP_EXEC : MS_FINISH;
                if (s_state == MS_STEP_EXEC) s_step = 0;   /* 循环执行 */
            } else {
                s_state = MS_STEP_EXEC;
            }
            break;

        case MS_STEP_EXEC: {
            const uint32_t wait_ms = exec_step(&s_def->steps[s_step]);
            if (wait_ms > 0) {
                s_deadline_us = esp_timer_get_time() + (int64_t)wait_ms * 1000;
                s_state = MS_WAIT_DELAY;
            } else {
                s_step++;
                s_state = MS_CHECK;
            }
            break;
        }

        case MS_WAIT_DELAY:
            if (esp_timer_get_time() >= s_deadline_us) {
                if (s_pending) {                 /* 敲击/组合键：到点释放 */
                    hid_kb_release(s_pending_key, s_pending_mod);
                    s_pending = false;
                }
                s_step++;
                s_state = MS_CHECK;
            }
            break;

        case MS_FINISH:
            release_everything();
            ESP_LOGI(TAG, "✔ 宏 %u 执行完成", s_macro_id);
            s_state = MS_IDLE;
            break;

        case MS_ABORT:
            /* ★ 关键：无论什么原因中止，都必须把宏按下的键全部释放 */
            release_everything();
            s_abort_cnt++;
            ESP_LOGW(TAG, "✗ 宏 %u 被中止（已释放全部按键）", s_macro_id);

            if (s_has_next) {           /* 中止是为了切换宏 → 接着启动新的 */
                s_has_next = false;
                start_macro(s_next_req);
            } else {
                s_state = MS_IDLE;
            }
            break;
        }

        /* ── 全局总超时保护（需求书 §4.4：30 秒强制终止并释放所有按键）── */
        if (s_state != MS_IDLE && s_state != MS_CHECK) {
            if (esp_timer_get_time() - s_start_us > s_timeout_us) {
                ESP_LOGE(TAG, "宏 %u 超过总超时 %lld ms，强制终止",
                         s_macro_id, (long long)(s_timeout_us / 1000));
                s_state = MS_ABORT;
            }
        }

        /* ★ 让出 CPU：5ms 步进。计时靠 esp_timer_get_time()，不依赖这个延时精度，
           所以调度抖动不会累积成延迟误差。这不是需求书禁止的"阻塞式 delay()"。 */
        vTaskDelay(pdMS_TO_TICKS(5));
    }
}

/* ══════════════════════════════════════════════════════════════
 *  对外接口
 * ══════════════════════════════════════════════════════════════ */

esp_err_t macro_engine_init(void)
{
    s_state    = MS_IDLE;
    s_def      = NULL;
    s_pending  = false;
    s_held_n   = 0;
    s_run_cnt = s_abort_cnt = s_step_cnt = 0;

    s_req_q = xQueueCreate(4, sizeof(uint8_t));
    if (!s_req_q) return ESP_ERR_NO_MEM;
    return ESP_OK;
}

esp_err_t macro_engine_start(void)
{
    if (!s_req_q) return ESP_ERR_INVALID_STATE;

    if (xTaskCreatePinnedToCore(macro_task, "macro", 4096, NULL, 5, NULL, 1) != pdPASS) {
        ESP_LOGE(TAG, "创建宏任务失败");
        return ESP_ERR_NO_MEM;
    }
    ESP_LOGI(TAG, "宏引擎已启动（非阻塞状态机，总超时 %d ms）", MACRO_TIMEOUT_MS);
    return ESP_OK;
}

esp_err_t macro_trigger(uint8_t macro_id)
{
    if (!s_req_q) return ESP_ERR_INVALID_STATE;

    /* ★ 纯非阻塞：只投递请求。
       是否要先中止当前宏、什么时候切换，全部由宏任务自己决定 ——
       绝对不能在调用者（hid_tx 任务，5ms 周期）里 vTaskDelay 等待。 */
    if (xQueueSend(s_req_q, &macro_id, 0) != pdTRUE) {
        ESP_LOGW(TAG, "宏请求队列满，丢弃");
        return ESP_ERR_NO_MEM;
    }
    return ESP_OK;
}

/**
 * ★ 触发按钮 / 上位机「测试触发」统一走这里。
 *
 * 规则：
 *   · 循环宏（flags bit0）——【开关式】：正在跑的正是它 → 停止；
 *                            否则（空闲 / 跑的是别的宏）→ 启动
 *   · 非循环宏          —— 【触发式】：每次都从头启动（正在跑就重来）
 *
 * 为什么循环宏要做成开关式：循环宏会一直重复跑，用户需要一个
 * "再按一下停"的手段。否则就只能靠 30 秒总超时自动收尾，很难用。
 */
esp_err_t macro_toggle_or_trigger(uint8_t macro_id, bool *stopped)
{
    if (stopped) *stopped = false;

    if (!s_req_q) return ESP_ERR_INVALID_STATE;

    /* 判断"正在跑的正是这个循环宏"。
       s_state / s_macro_id 都是 volatile，这里读到的是某一瞬间的值：
       万一宏任务正好在同时收尾，最坏结果只是把已经结束的宏再中止一次
       （macro_abort 对 IDLE 是空操作），不会出错。 */
    if (s_state != MS_IDLE &&
        s_macro_id == macro_id &&
        s_def != NULL &&
        (s_def->flags & KB_MACRO_FLAG_LOOP)) {
        /* ★★ 这里**一句日志都不能打**。
         *
         * 本函数曾被 keymap_apply() 在 portENTER_CRITICAL() 里调用过，
         * 而 ESP_LOGI 要写串口、**串口写会阻塞** ——
         * 在临界区里阻塞会让 FreeRTOS 断言失败、设备直接复位。
         * 实测现象：循环宏按第一下开始正常，**按第二下想停止时 HID 重新枚举**。
         *
         * 现在调用方已经改成出了临界区再调，但这个接口不该依赖调用者的上下文 ——
         * 谁知道以后会不会又有人在锁里调它。所以日志从这里彻底去掉：
         * 要记录就让宏任务自己记，它知道状态从 RUN 变成了 ABORT。
         */
        macro_abort();
        if (stopped) *stopped = true;
        return ESP_OK;
    }

    return macro_trigger(macro_id);
}

void macro_abort(void)
{
    if (s_state != MS_IDLE) s_state = MS_ABORT;
}

void macro_reload(void)
{
    /* 中止当前宏 —— 定义变了，继续跑旧的就错位了。
       不在这里等待：宏任务会自行跑到 IDLE。 */
    if (s_state != MS_IDLE) macro_abort();
    ESP_LOGI(TAG, "宏定义已重载（如有正在执行的宏会被中止）");
}

bool     macro_is_busy(void)      { return s_state != MS_IDLE; }
uint8_t  macro_current(void)      { return (s_state != MS_IDLE) ? s_macro_id : 0xFF; }
uint32_t macro_run_count(void)    { return s_run_cnt; }
uint32_t macro_abort_count(void)  { return s_abort_cnt; }
uint32_t macro_step_count(void)   { return s_step_cnt; }
