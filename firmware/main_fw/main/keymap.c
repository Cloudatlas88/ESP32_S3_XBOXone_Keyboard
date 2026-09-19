/**
 * @file keymap.c
 * @brief 输入 → 键盘按键映射（增量化驱动，避免重复发报告）
 */
#include <string.h>

#include "freertos/FreeRTOS.h"      /* portMUX_TYPE / portENTER_CRITICAL */
#include "esp_log.h"

#include "app_config.h"
#include "hid_keyboard.h"
#include "macro_engine.h"
#include "keymap.h"

static portMUX_TYPE s_mux = portMUX_INITIALIZER_UNLOCKED;

typedef struct {
    uint8_t key;
    uint8_t mod;
    bool    active;
} km_src_t;

static km_src_t s_src[KB_SRC_COUNT];

/* 各槽位上一帧的按下状态 —— 宏触发需要【按下边沿】检测 */
static bool s_slot_prev[SLOT_COUNT];

static void src_update(int idx, bool want, uint8_t key, uint8_t mod)
{
    km_src_t *s = &s_src[idx];

    if (want) {
        /* 还按着但目标键变了（上位机改了映射）→ 先松开旧键 */
        if (s->active && (s->key != key || s->mod != mod)) {
            hid_kb_release(s->key, s->mod);   /* ★ 修饰键要一起减回去 */
            s->active = false;
        }
        if (!s->active && key) {
            hid_kb_press(key, mod);
            s->active = true;
            s->key    = key;
            s->mod    = mod;
        }
    } else if (s->active) {
        hid_kb_release(s->key, s->mod);       /* ★ 同上：不传 mod 会让 Ctrl 卡住 */
        s->active = false;
    }
}

void keymap_reset(void)
{
    portENTER_CRITICAL(&s_mux);
    for (int i = 0; i < KB_SRC_COUNT; i++) {
        if (s_src[i].active) {
            hid_kb_release(s_src[i].key, s_src[i].mod);
            s_src[i].active = false;
        }
    }
    portEXIT_CRITICAL(&s_mux);

    /* 边沿检测状态也要清：否则重置后第一次按下可能不触发宏 */
    memset(s_slot_prev, 0, sizeof(s_slot_prev));
}

void keymap_apply(const input_state_t *st, const kb_config_t *cfg)
{
    /* ★★ 本帧要触发的宏先记下来，**出了临界区再投递**。
     *
     * 为什么不能像以前那样在临界区里直接调 macro_toggle_or_trigger：
     *   它内部有一句 ESP_LOGI（"循环宏正在运行 → 再按一次停止它"），
     *   而**串口写是会阻塞的**。FreeRTOS 不允许在临界区里阻塞 ——
     *   会断言失败直接复位，表现就是"按第二下停止宏，Windows 那边 HID 重新枚举"。
     *
     *   也解释了为什么只有"停"会崩、"开"不会：
     *   开走的是 macro_trigger，里面只有一句非阻塞的 xQueueSend，没有日志。
     *
     *   顺带把 xQueueSend 也挪出去了 —— 队列操作在临界区里同样不该做
     *   （它可能触发任务切换，等于在关掉调度器的时候要求调度）。
     *
     * ★ 规矩：临界区里只碰内存；日志 / 队列 / 延时一律放到外面。
     *   macro_engine 那边也一并去掉了日志，不让这个接口依赖调用者的上下文。
     */
    uint8_t pending[SLOT_COUNT];
    int     pending_n = 0;

    portENTER_CRITICAL(&s_mux);

    for (int i = 0; i < SLOT_COUNT; i++) {
        const kb_button_cfg_t *b = &cfg->buttons[i];
        const bool enabled = (b->flags & KB_BTNFLAG_ENABLED) != 0;
        const bool pressed = ((st->slots >> i) & 1) != 0;
        const bool prev    = s_slot_prev[i];
        s_slot_prev[i] = pressed;

        if (!enabled) {
            src_update(i, false, 0, 0);
            continue;
        }

        if ((b->flags & KB_BTNFLAG_HAS_MACRO) && b->macro_id < MACRO_MAX) {
            /* 该槽位触发宏：只在【按下边沿】触发一次，本身不参与单键映射。 */
            src_update(i, false, 0, 0);
            if (pressed && !prev && pending_n < SLOT_COUNT) {
                pending[pending_n++] = b->macro_id;
            }
            continue;
        }

        /* 普通按键映射。
           ★ 和实体键盘完全一致：按住 → 键保持按下（主机自己重复）；
             松开 → 键抬起。不再看 b->trigger（那个概念已移除）。 */
        src_update(i, pressed, b->keycode, b->modifiers);
    }

    portEXIT_CRITICAL(&s_mux);

    /* ── 出了临界区，这时才允许碰宏引擎 ──
       ★ 走 macro_toggle_or_trigger 而不是 macro_trigger：
         勾了「循环执行」的宏是开关式 —— 按一下开始、再按一下停。 */
    for (int i = 0; i < pending_n; i++) {
        macro_toggle_or_trigger(pending[i], NULL);
    }
}

int keymap_active_count(void)
{
    int n = 0;
    portENTER_CRITICAL(&s_mux);
    for (int i = 0; i < KB_SRC_COUNT; i++) if (s_src[i].active) n++;
    portEXIT_CRITICAL(&s_mux);
    return n;
}
