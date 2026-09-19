/**
 * @file hid_keyboard.c
 * @brief HID 键盘报告单点出口（引用计数 + 6 键溢出处理）
 */
#include <string.h>

#include "esp_log.h"
#include "tinyusb.h"
#include "class/hid/hid_device.h"

#include "app_config.h"
#include "hid_keyboard.h"

static const char *TAG = "hid_kb";

/* ── keyset ── */
static portMUX_TYPE s_mux = portMUX_INITIALIZER_UNLOCKED;

static uint8_t  s_refcount[256];       /* 每个键码的引用计数 */
static uint8_t  s_mod_ref[8];          /* ★ 每个修饰键位的引用计数（见 hid_kb_release）*/
static uint8_t  s_modifiers;
static uint8_t  s_leds;
static bool     s_mounted;
static bool     s_dirty = true;        /* 需要重发报告 */
static bool     s_overflow;

static volatile uint32_t s_overflow_cnt;
static volatile uint32_t s_report_cnt;

/* ══════════════════════════════════════════════════════════════
 *  内部：组包
 * ══════════════════════════════════════════════════════════════ */

/** 按引用计数组装 6 槽键码数组；超过 6 个则置 ErrorRollOver */
static void build_keycodes(uint8_t out[KB_KEYSET_SLOTS])
{
    memset(out, 0, KB_KEYSET_SLOTS);

    int n = 0;
    for (int k = 1; k < 256 && n < KB_KEYSET_SLOTS; k++) {
        if (s_refcount[k] > 0) out[n++] = (uint8_t)k;
    }

    /* 还有没装下的键 → 溢出 */
    bool more = false;
    for (int k = 1; k < 256; k++) {
        if (s_refcount[k] > 0) {
            more = true;
            for (int i = 0; i < KB_KEYSET_SLOTS; i++) if (out[i] == k) { more = false; break; }
            if (more) break;
        }
    }

    if (more) {
        /* HID 规范：同时按下超过槽位数时填 ErrorRollOver */
        for (int i = 0; i < KB_KEYSET_SLOTS; i++) out[i] = 0x01;
        if (!s_overflow) {
            s_overflow = true;
            s_overflow_cnt++;
            ESP_LOGW(TAG, "同时按下超过 %d 键 → ErrorRollOver", KB_KEYSET_SLOTS);
        }
    } else {
        s_overflow = false;
    }
}

/* ══════════════════════════════════════════════════════════════
 *  对外接口
 * ══════════════════════════════════════════════════════════════ */

esp_err_t hid_kb_init(void)
{
    memset(s_refcount, 0, sizeof(s_refcount));
    s_modifiers = 0;
    s_leds      = 0;
    s_mounted   = false;
    s_dirty     = true;
    s_overflow  = false;
    return ESP_OK;
}

bool hid_kb_press(uint8_t keycode, uint8_t modifiers)
{
    portENTER_CRITICAL(&s_mux);

    if (keycode) {
        if (s_refcount[keycode] < 0xFF) s_refcount[keycode]++;
    }

    /* ★ 修饰键按下时同样累加引用计数（不能只置位）——
       否则释放时无从判断"还有没有别的来源需要它"。 */
    for (int b = 0; b < 8; b++) {
        if (modifiers & (1u << b)) {
            if (s_mod_ref[b] < 0xFF) s_mod_ref[b]++;
            s_modifiers |= (uint8_t)(1u << b);
        }
    }

    s_dirty = true;
    portEXIT_CRITICAL(&s_mux);
    return true;
}

/**
 * 释放一个键。
 *
 * ★ 修饰键必须在这里按引用计数减回去，减到 0 才把报告里的对应位清掉。
 *
 *   踩过的坑（实测确认）：早先的签名是 release(keycode)，拿不到修饰键，
 *   而 press 里是简单的 `s_modifiers |= modifiers`、release 里完全不动它 ——
 *   于是把按钮映射成 Ctrl+A 之后，按一下再松开，
 *   **左Ctrl 会永久卡在按下状态**，主机之后收到的每个键都变成 Ctrl+某键，
 *   直到 USB 重新枚举或设备重启才恢复。宏的 COMBO 步骤同样会漏。
 *
 *   实体键盘松开 Ctrl 时报告里那一位就清了，所以修完才和实体键盘一致。
 */
bool hid_kb_release(uint8_t keycode, uint8_t modifiers)
{
    portENTER_CRITICAL(&s_mux);

    if (keycode && s_refcount[keycode] > 0) {
        s_refcount[keycode]--;
        s_dirty = true;
    }

    for (int b = 0; b < 8; b++) {
        if (!(modifiers & (1u << b))) continue;
        if (s_mod_ref[b] > 0) s_mod_ref[b]--;
        if (s_mod_ref[b] == 0) {
            s_modifiers &= (uint8_t)~(1u << b);
            s_dirty = true;
        }
    }

    portEXIT_CRITICAL(&s_mux);
    return true;
}

void hid_kb_release_all(void)
{
    portENTER_CRITICAL(&s_mux);
    memset(s_refcount, 0, sizeof(s_refcount));
    memset(s_mod_ref,  0, sizeof(s_mod_ref));
    s_modifiers = 0;
    s_dirty     = true;
    portEXIT_CRITICAL(&s_mux);
    ESP_LOGD(TAG, "已释放全部按键");
}

void hid_kb_flush(void)
{
    uint8_t keys[KB_KEYSET_SLOTS];
    uint8_t mod;

    portENTER_CRITICAL(&s_mux);
    if (!s_dirty) { portEXIT_CRITICAL(&s_mux); return; }
    build_keycodes(keys);
    mod     = s_modifiers;
    s_dirty = false;
    portEXIT_CRITICAL(&s_mux);

    if (!tud_mounted()) return;

    tud_hid_n_keyboard_report(KB_ITF_KEYBOARD, KB_HID_RID_KEYBOARD, mod, keys);
    s_report_cnt++;
}

bool     hid_kb_is_mounted(void)      { return s_mounted; }
uint8_t  hid_kb_get_leds(void)        { return s_leds; }
uint32_t hid_kb_overflow_count(void)  { return s_overflow_cnt; }
uint32_t hid_kb_report_count(void)    { return s_report_cnt; }

uint8_t hid_kb_pressed_count(void)
{
    uint8_t n = 0;
    portENTER_CRITICAL(&s_mux);
    for (int k = 1; k < 256; k++) if (s_refcount[k] > 0) n++;
    portEXIT_CRITICAL(&s_mux);
    return n;
}

uint8_t hid_kb_modifiers(void)
{
    uint8_t m;
    portENTER_CRITICAL(&s_mux);
    m = s_modifiers;
    portEXIT_CRITICAL(&s_mux);
    return m;
}

void hid_kb_on_mount(void)
{
    hid_kb_release_all();

    s_mounted = true;
    s_dirty   = true;

    /* ★ 需求书 §6.6 的正确实现：枚举完成后立刻发一次全零报告，
       确保主机端不会残留上一次的按键状态（枚举前不可能发，那时没有可用端点）*/
    hid_kb_flush();

    ESP_LOGI(TAG, "USB 已挂载 → 已发送全零报告，主机端按键状态归零");
}

void hid_kb_on_umount(void)
{
    s_mounted = false;
    /* 断线时报告发不出去，只清内部状态，避免重连后残留 */
    portENTER_CRITICAL(&s_mux);
    memset(s_refcount, 0, sizeof(s_refcount));
    memset(s_mod_ref,  0, sizeof(s_mod_ref));
    s_modifiers = 0;
    s_dirty     = true;
    portEXIT_CRITICAL(&s_mux);
}

void hid_kb_on_led_report(uint8_t led_byte)
{
    s_leds = led_byte;
    ESP_LOGI(TAG, "LED 状态: 0x%02X  Num=%d Caps=%d Scroll=%d",
             led_byte, !!(led_byte & 0x01), !!(led_byte & 0x02), !!(led_byte & 0x04));
}
