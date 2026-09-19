/**
 * @file main.c
 * @brief ESP32-S3 手柄改 USB HID 键盘 —— 主程序
 *
 * 已实现：
 *   · 双 HID 接口复合设备（MI_00 键盘 / MI_01 厂商配置通道）
 *   · 摇杆 ADC + 五键扫描（1ms，含死区/迟滞/防抖/悬空轴禁用）
 *   · 摇杆/按键 → 键盘映射（引用计数 keyset，6 键溢出保护）
 *   · Input Report 实时上报（20Hz，供上位机模拟调试界面）
 *   · Feature Report 配置协议（分片 + CRC16）
 *   · NVS 双槽存储 + 60 秒延迟批量写入
 *
 * 待实现：宏引擎、任务看门狗、LED 空闲报告
 */
#include <string.h>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "driver/gpio.h"
#include "esp_log.h"
#include "esp_system.h"
#include "esp_timer.h"
#include "tinyusb.h"
#include "tinyusb_default_config.h"
#include "class/hid/hid_device.h"

#include "app_config.h"
#include "usb_descriptors.h"
#include "input_scan.h"
#include "hid_keyboard.h"
#include "hid_config.h"
#include "keymap.h"
#include "macro_engine.h"
#include "nvs_config.h"

static const char *TAG = "main_fw";

static volatile bool s_mounted = false;

/* ══════════════════════════════════════════════════════════════
 *  TinyUSB 回调
 * ══════════════════════════════════════════════════════════════ */

/** 返回对应接口的报告描述符（instance 即接口索引）*/
const uint8_t *tud_hid_descriptor_report_cb(uint8_t instance)
{
    return (instance == KB_ITF_KEYBOARD) ? hid_report_desc_keyboard
                                         : hid_report_desc_vendor;
}

/**
 * GET_REPORT
 * ★ TinyUSB 已把 Report ID 写进线上缓冲区并前移了指针，
 *   所以这里的 buffer 就是负载起始，返回值也只是【负载长度】。
 */
uint16_t tud_hid_get_report_cb(uint8_t instance, uint8_t report_id,
                               hid_report_type_t report_type,
                               uint8_t *buffer, uint16_t reqlen)
{
    if (instance != KB_ITF_CONFIG || report_type != HID_REPORT_TYPE_FEATURE) {
        return 0;                       /* 键盘接口不实现 Feature Report */
    }
    return hid_config_get_report(report_id, buffer, reqlen);
}

/**
 * SET_REPORT
 *   · 键盘接口的 Output 报告 = 主机下发的 LED 状态
 *   · 厂商接口的 Feature 报告 = 上位机的配置命令
 */
void tud_hid_set_report_cb(uint8_t instance, uint8_t report_id,
                           hid_report_type_t report_type,
                           uint8_t const *buffer, uint16_t bufsize)
{
    if (instance == KB_ITF_KEYBOARD && report_type == HID_REPORT_TYPE_OUTPUT && bufsize >= 1) {
        hid_kb_on_led_report(buffer[0]);
        return;
    }
    if (instance == KB_ITF_CONFIG && report_type == HID_REPORT_TYPE_FEATURE) {
        hid_config_set_report(report_id, buffer, bufsize);
        return;
    }
}

/**
 * USB 设备事件
 * ⚠️ esp_tinyusb 组件自己实现了 tud_mount_cb/umount/suspend/resume，
 *    应用层不能再定义（会 multiple definition），必须走事件回调。
 */
static void tinyusb_event_handler(tinyusb_event_t *event, void *arg)
{
    (void)arg;

    switch (event->id) {
    case TINYUSB_EVENT_ATTACHED:
        s_mounted = true;
        input_scan_set_mounted(true);
        hid_kb_on_mount();              /* 清空 + 发全零报告 */
        break;

    case TINYUSB_EVENT_DETACHED:
        s_mounted = false;
        input_scan_set_mounted(false);
        hid_kb_on_umount();
        keymap_reset();                 /* 断开时释放全部映射源 */
        ESP_LOGW(TAG, "USB 已断开 → 按键状态作废");
        break;

#ifdef CONFIG_TINYUSB_SUSPEND_CALLBACK
    case TINYUSB_EVENT_SUSPENDED:
        s_mounted = false;
        input_scan_set_mounted(false);
        hid_kb_on_umount();
        keymap_reset();
        ESP_LOGW(TAG, "USB 挂起 (远程唤醒=%d)", (int)event->suspended.remote_wakeup);
        break;
#endif

#ifdef CONFIG_TINYUSB_RESUME_CALLBACK
    case TINYUSB_EVENT_RESUMED:
        s_mounted = true;
        input_scan_set_mounted(true);
        hid_kb_on_mount();
        ESP_LOGI(TAG, "USB 已恢复");
        break;
#endif

    default:
        break;
    }
}

/* ══════════════════════════════════════════════════════════════
 *  任务 1：键盘输出（5ms）
 *     输入状态 → 配置映射 → HID 报告
 * ══════════════════════════════════════════════════════════════ */

static void hid_tx_task(void *arg)
{
    (void)arg;
    TickType_t last_wake = xTaskGetTickCount();
    uint32_t   epoch     = 0;
    kb_config_t cfg;

    nvs_config_snapshot(&cfg);

    while (1) {
        input_state_t st;
        input_scan_get_state(&st);

        /* 配置被上位机改过 → 先松开全部键再按新映射，避免卡键 */
        const uint32_t e = nvs_config_epoch();
        if (e != epoch) {
            keymap_reset();
            macro_reload();             /* 宏定义也变了，中止正在跑的宏 */
            nvs_config_snapshot(&cfg);
            epoch = e;
            ESP_LOGI(TAG, "映射已重载 (epoch=%lu)", (unsigned long)e);
        }

        if (s_mounted) {
            keymap_apply(&st, &cfg);
        } else {
            keymap_reset();
        }

        hid_kb_flush();
        vTaskDelayUntil(&last_wake, pdMS_TO_TICKS(5));
    }
}

/* ══════════════════════════════════════════════════════════════
 *  任务 2：输入状态实时上报（20Hz）
 *     供上位机「按键布局」页实时显示摇杆/按钮状态
 *
 *  注意：这里【逐字段手工打包】，不能直接 memcpy input_state_t ——
 *        结构体有对齐填充，而上位机是按固定偏移解析的。
 *
 *  ★ 负载长度与全部偏移定义在 app_config.h 的 IN_OFF_*（52 字节）——
 *    那里是唯一事实来源，每一段的边界都有 _Static_assert 钉着
 *    （改错一格编译就过不去）。上位机对应 InputState.cs。
 *    规格文档：项目结构.md §六
 * ══════════════════════════════════════════════════════════════ */

static void input_report_task(void *arg)
{
    (void)arg;
    TickType_t last_wake = xTaskGetTickCount();

    while (1) {
        if (tud_mounted()) {
            input_state_t st;
            input_scan_get_state(&st);

            uint8_t p[KB_INPUT_PAYLOAD] = { 0 };

            /* ★ 偏移表在 app_config.h 的 IN_OFF_* —— 那里是唯一事实来源，
               每段的边界都有 _Static_assert 钉着（错一格编译就过不去）。 */
            p[IN_OFF_FLAGS]   = st.flags;

            /* 26 个槽位，4 字节小端 */
            p[IN_OFF_BUTTONS + 0] = (uint8_t)(st.slots & 0xFF);
            p[IN_OFF_BUTTONS + 1] = (uint8_t)((st.slots >> 8) & 0xFF);
            p[IN_OFF_BUTTONS + 2] = (uint8_t)((st.slots >> 16) & 0xFF);
            p[IN_OFF_BUTTONS + 3] = (uint8_t)((st.slots >> 24) & 0xFF);

            /* 三组方向位 —— 从槽位位图翻译出来，保证和按键位**不可能不一致** */
            p[IN_OFF_DIRS_DPAD]   = slots_to_dirs(st.slots, DIRBASE_DPAD);
            p[IN_OFF_DIRS_LSTICK] = slots_to_dirs(st.slots, DIRBASE_LSTICK);
            p[IN_OFF_DIRS_RSTICK] = slots_to_dirs(st.slots, DIRBASE_RSTICK);

            p[IN_OFF_STAT] = (uint8_t)(macro_is_busy() ? KB_STAT_MACRO_BUSY : 0);

            /* 4 轴：原始值 / 中心 / 行程极值（都是 uint16 小端）*/
            for (int a = 0; a < AXIS_COUNT; a++) {
                const int ro = IN_OFF_RAW    + a * 2;
                const int co = IN_OFF_CENTER + a * 2;
                const int to = IN_OFF_TRAVEL + a * 4;
                p[ro + 0] = (uint8_t)(st.raw[a]    & 0xFF);
                p[ro + 1] = (uint8_t)(st.raw[a]    >> 8);
                p[co + 0] = (uint8_t)(st.center[a] & 0xFF);
                p[co + 1] = (uint8_t)(st.center[a] >> 8);
                p[to + 0] = (uint8_t)(st.tmin[a]   & 0xFF);
                p[to + 1] = (uint8_t)(st.tmin[a]   >> 8);
                p[to + 2] = (uint8_t)(st.tmax[a]   & 0xFF);
                p[to + 3] = (uint8_t)(st.tmax[a]   >> 8);
            }

            p[IN_OFF_UPTIME + 0] = (uint8_t)(st.uptime_ms & 0xFF);
            p[IN_OFF_UPTIME + 1] = (uint8_t)((st.uptime_ms >> 8) & 0xFF);
            p[IN_OFF_UPTIME + 2] = (uint8_t)((st.uptime_ms >> 16) & 0xFF);
            p[IN_OFF_UPTIME + 3] = (uint8_t)((st.uptime_ms >> 24) & 0xFF);

            p[IN_OFF_SEQ + 0] = (uint8_t)(st.seq & 0xFF);
            p[IN_OFF_SEQ + 1] = (uint8_t)((st.seq >> 8) & 0xFF);
            p[IN_OFF_SEQ + 2] = (uint8_t)((st.seq >> 16) & 0xFF);
            p[IN_OFF_SEQ + 3] = (uint8_t)((st.seq >> 24) & 0xFF);

            p[IN_OFF_AXIS_FAULT] = st.axis_fault;      /* bit0..3 = LX/LY/RX/RY */

            /* ★ 24 个槽位的原始 GPIO 电平（未防抖）—— 接线诊断的关键字段 */
            memcpy(&p[IN_OFF_BTN_RAW], st.btn_raw, 4);

            tud_hid_n_report(KB_ITF_CONFIG, KB_RID_INPUT, p, sizeof(p));
        }
        vTaskDelayUntil(&last_wake, pdMS_TO_TICKS(KB_INPUT_INTERVAL_MS));
    }
}

/* ══════════════════════════════════════════════════════════════
 *  BOOT 按键：打一个 'a' 验证键盘通路（走 hid_kb，与映射共用出口）
 * ══════════════════════════════════════════════════════════════ */

static void boot_button_init(void)
{
    const gpio_config_t cfg = {
        .pin_bit_mask = 1ULL << BOOT_BUTTON_GPIO,
        .mode         = GPIO_MODE_INPUT,
        .pull_up_en   = GPIO_PULLUP_ENABLE,
        .pull_down_en = GPIO_PULLDOWN_DISABLE,
        .intr_type    = GPIO_INTR_DISABLE,
    };
    ESP_ERROR_CHECK(gpio_config(&cfg));
}

/* ══════════════════════════════════════════════════════════════
 *  入口
 * ══════════════════════════════════════════════════════════════ */

static void log_reset_reason(void)
{
    const char *r = "unknown";
    switch (esp_reset_reason()) {
    case ESP_RST_POWERON:  r = "power-on";       break;
    case ESP_RST_SW:       r = "software";       break;
    case ESP_RST_PANIC:    r = "PANIC";          break;
    case ESP_RST_TASK_WDT: r = "TASK WATCHDOG";  break;
    case ESP_RST_INT_WDT:  r = "INT WATCHDOG";   break;
    case ESP_RST_WDT:      r = "other WATCHDOG"; break;
    case ESP_RST_USB:      r = "USB peripheral"; break;
    case ESP_RST_BROWNOUT: r = "brownout";       break;
    default: break;
    }
    ESP_LOGI(TAG, "上次复位原因: %s", r);
}

void app_main(void)
{
    ESP_LOGI(TAG, "════════ %s %s ════════", APP_NAME, FW_VERSION_STR);
    log_reset_reason();

    ESP_LOGI(TAG, "设备标识: %04X:%04X  \"%s\" / \"%s\"  序列号=不报告",
             KB_VID, KB_PID, KB_STR_MANUFACTURER, KB_STR_PRODUCT);

    /* ── 1. 输入采集（ADC 初始化 + 摇杆中心自校准）── */
    ESP_ERROR_CHECK(input_scan_init());
    ESP_ERROR_CHECK(input_scan_start());

    /* ── 2. 配置存储（NVS 双槽 + 延迟写入任务）── */
    ESP_ERROR_CHECK(nvs_config_init());
    ESP_ERROR_CHECK(nvs_config_start());

    /* ── 3. 键盘报告层 + 配置协议 + 宏引擎 ── */
    ESP_ERROR_CHECK(hid_kb_init());
    ESP_ERROR_CHECK(hid_config_init());
    ESP_ERROR_CHECK(macro_engine_init());

    boot_button_init();

    /* ── 4. USB 设备 ── */
    tinyusb_config_t tusb_cfg = TINYUSB_DEFAULT_CONFIG(tinyusb_event_handler);
    tusb_cfg.descriptor.device            = &desc_device;
    tusb_cfg.descriptor.full_speed_config = desc_configuration;
    tusb_cfg.descriptor.string            = desc_string_table;
    tusb_cfg.descriptor.string_count      = desc_string_count;

    ESP_ERROR_CHECK(tinyusb_driver_install(&tusb_cfg));
    ESP_LOGI(TAG, "TinyUSB 已安装：MI_%02X 键盘 / MI_%02X 配置通道",
             KB_ITF_KEYBOARD, KB_ITF_CONFIG);

    /* ── 5. 任务 ── */
    if (xTaskCreatePinnedToCore(hid_tx_task, "hid_tx", 4096, NULL, 9, NULL, 0) != pdPASS) {
        ESP_LOGE(TAG, "创建 hid_tx 任务失败");
    }
    if (xTaskCreatePinnedToCore(input_report_task, "input_rpt", 3072, NULL, 5, NULL, 1) != pdPASS) {
        ESP_LOGE(TAG, "创建上报任务失败");
    }
    ESP_ERROR_CHECK(macro_engine_start());

    /* ── 6. 主循环：状态日志 + BOOT 键测试 ── */
    bool     prev_btn = false;
    /* 数字输入原始电平：当前值 / 已打日志的值 / 两个时刻（变化、上次打日志）*/
    static uint8_t s_raw_prev[4]   = { 0, 0, 0, 0 };
    static uint8_t s_raw_logged[4] = { 0xFF, 0xFF, 0xFF, 0xFF };   /* 初值不等，保证第一帧会报一次 */
    int64_t s_raw_change_ms = 0;
    int64_t s_raw_log_ms    = 0;

    uint32_t tick     = 0;
    uint32_t epoch    = 0;

    while (1) {
        /* ── 数字输入原始电平变化时打日志（诊断接线）──
         * 某个按键"没反应"时，靠它一刀切分清是接线还是软件：
         *   bit 变了   → 引脚收到了电平变化，问题在软件（映射/配置）
         *   bit 不变   → 引脚根本没收到信号，问题在接线或引脚功能
         *
         * ★ 这段原来在**输入扫描任务**里（1ms 热循环）。串口写一行要 5ms，
         *   等于每次按键都把扫描任务卡 5ms、连带饿死 main_task / TinyUSB ——
         *   实测被"扫描超时"告警抓了出来。搬到这个 5ms 周期的低频任务里。
         *
         * ★★ 还必须**等电平稳定 + 限流**。
         *   某个引脚接触不良时它会以几十 Hz 抖动，而这里每看到一次变化就打一行 ——
         *   结果 50 秒的日志里全是同一行在刷，真正的启动信息全被冲掉，
         *   反而查不出问题（实测踩过）。抖动本身是**接线问题**的信号，
         *   值得报一次，但不值得把串口占满。
         */
        {
            input_state_t cur;
            input_scan_get_state(&cur);
            const int64_t now_ms = esp_timer_get_time() / 1000;

            if (memcmp(cur.btn_raw, s_raw_prev, 4) != 0) {
                memcpy(s_raw_prev, cur.btn_raw, 4);
                s_raw_change_ms = now_ms;      /* 记下最近一次变化的时刻 */
            }

            /* 电平已经稳定 100ms（不是还在抖），且距上次打日志 ≥300ms */
            if (memcmp(s_raw_prev, s_raw_logged, 4) != 0
                && now_ms - s_raw_change_ms >= 100
                && now_ms - s_raw_log_ms >= 300) {
                ESP_LOGI(TAG, "数字输入原始电平 %02X%02X%02X%02X → %02X%02X%02X%02X",
                         s_raw_logged[3], s_raw_logged[2], s_raw_logged[1], s_raw_logged[0],
                         s_raw_prev[3], s_raw_prev[2], s_raw_prev[1], s_raw_prev[0]);
                memcpy(s_raw_logged, s_raw_prev, 4);
                s_raw_log_ms = now_ms;
            }
        }

        if (++tick % 1000 == 0) {       /* 每 5 秒 */
            input_state_t st;
            input_scan_get_state(&st);
            kb_config_t snap;
            nvs_config_snapshot(&snap);

            ESP_LOGI(TAG, "状态 mnt=%d LED=0x%02X | "
                          "L(%4u %+4d%%, %4u %+4d%%) R(%4u %+4d%%, %4u %+4d%%) | "
                          "槽位=%06lX | 按键中=%u 报告=%lu 溢出=%lu | "
                          "cfg: 死区%u/%u%% 宏%u 脏=%d 落盘失败=%lu | "
                          "宏: %s 运行%lu 中止%lu | CMD %lu/%lu",
                     (int)s_mounted, hid_kb_get_leds(),
                     st.raw[AXIS_LX], st.pct[AXIS_LX], st.raw[AXIS_LY], st.pct[AXIS_LY],
                     st.raw[AXIS_RX], st.pct[AXIS_RX], st.raw[AXIS_RY], st.pct[AXIS_RY],
                     (unsigned long)(st.slots & 0xFFFFFF),
                     hid_kb_pressed_count(), (unsigned long)hid_kb_report_count(),
                     (unsigned long)hid_kb_overflow_count(),
                     snap.stick[STICK_L].deadzone_pct, snap.stick[STICK_R].deadzone_pct,
                     snap.macro_count,
                     (int)nvs_config_is_dirty(), (unsigned long)nvs_config_save_fail(),
                     macro_is_busy() ? "执行中" : "空闲",
                     (unsigned long)macro_run_count(), (unsigned long)macro_abort_count(),
                     (unsigned long)hid_config_set_count(),
                     (unsigned long)hid_config_get_count());
        }

        /* BOOT 键：短按发一个 'a'（走 hid_kb，和映射共用同一个出口）*/
        if (s_mounted) {
            const bool btn = (gpio_get_level(BOOT_BUTTON_GPIO) == 0);
            if (btn && !prev_btn)  hid_kb_press(HID_KEY_A, 0);
            if (!btn && prev_btn)  hid_kb_release(HID_KEY_A, 0);
            prev_btn = btn;
        }

        (void)epoch;
        vTaskDelay(pdMS_TO_TICKS(5));
    }
}
