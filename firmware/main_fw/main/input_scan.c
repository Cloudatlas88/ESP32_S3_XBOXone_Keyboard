/**
 * @file input_scan.c
 * @brief 16 路数字输入 + 2 个摇杆（4 路 ADC）扫描实现（0x0004 / 24 槽位）
 *
 * 设计要点
 *   · 1ms 周期用 vTaskDelayUntil 保证等间隔（不用 esp_timer 回调，避免在中断上下文做 ADC）
 *   · 每周期每轴采 8 次取【中位数】——中位数对单次脉冲干扰的抑制远好于平均值
 *   · 摇杆中心【开机自校准】：采样 200 次取平均，自动适应电位器公差与机械中位偏差
 *   · 死区 + 迟滞：进入阈值 = 死区，退出阈值 = 死区 - 4%，彻底消除边界抖动
 *   · 数字输入【计数防抖】：连续 5 次扫描电平一致才翻转（= 5ms 窗口）
 *   · ADC 贴轨检测：只在【开机自校准】时判（中心贴轨 / 读数跨度异常），
 *     运行期不判 —— 推到底本来就会读到 4095，运行期判会误伤（踩过）
 *
 * ★ 0x0004 的两处结构性变化
 *   1. 输入源从「4 方向 + 5 按钮」两套，统一成 **24 个槽位**一个位图
 *   2. 摇杆从 1 个变 2 个，**每摇杆独立死区/反向** —— 这两个值由配置下发，
 *      不再是编译期常量（原来界面上改死区根本不生效，配置字段读了没人用）
 */
#include <string.h>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "driver/gpio.h"
#include "esp_adc/adc_oneshot.h"
#include "esp_log.h"
#include "esp_timer.h"

#include "input_scan.h"

static const char *TAG = "input";

/* ══════════════════════════════════════════════════════════════
 *  配置下发的摇杆参数
 * ══════════════════════════════════════════════════════════════ */

typedef struct {
    uint8_t deadzone_pct;
    bool    invert_x;
    bool    invert_y;
} stick_param_t;

static stick_param_t s_sp[STICK_COUNT] = {
    { STICK_DEADZONE_PCT, false, false },
    { STICK_DEADZONE_PCT, false, false },
};

void input_scan_set_stick_params(int stick, uint8_t deadzone_pct,
                                 bool invert_x, bool invert_y)
{
    if (stick < 0 || stick >= STICK_COUNT) return;
    s_sp[stick].deadzone_pct = deadzone_pct;
    s_sp[stick].invert_x     = invert_x;
    s_sp[stick].invert_y     = invert_y;
    ESP_LOGI(TAG, "摇杆%d 参数更新：死区 %u%%%s%s",
             stick, deadzone_pct, invert_x ? " X反向" : "", invert_y ? " Y反向" : "");
}

/* ══════════════════════════════════════════════════════════════
 *  ADC（4 轴：LX LY RX RY）
 * ══════════════════════════════════════════════════════════════ */

static adc_oneshot_unit_handle_t s_adc;

/* 只用 ADC1。ADC2 将来开 WiFi 会冲突，留条后路。 */
static const adc_channel_t s_chan[AXIS_COUNT] = {
    ADC_CHANNEL_0,      /* AXIS_LX = GPIO1 */
    ADC_CHANNEL_1,      /* AXIS_LY = GPIO2 */
    ADC_CHANNEL_3,      /* AXIS_RX = GPIO4 */
    ADC_CHANNEL_4,      /* AXIS_RY = GPIO5 */
};

static const char *const s_axis_name[AXIS_COUNT] = {
    "左摇杆X(GPIO1)", "左摇杆Y(GPIO2)", "右摇杆X(GPIO4)", "右摇杆Y(GPIO5)"
};

/* ── 共享状态（临界区保护）── */
static portMUX_TYPE s_mux = portMUX_INITIALIZER_UNLOCKED;
static input_state_t s_state;

/* ── 摇杆校准与滤波 ── */
static uint16_t s_center[AXIS_COUNT] = { 2048, 2048, 2048, 2048 };
static uint8_t  s_dir_bits[STICK_COUNT] = { 0, 0 };
static uint8_t  s_axis_fault = 0;    /* bit0..3 = LX/LY/RX/RY 故障，故障轴被禁用 */

/* ── ★ 行程自学习 ──
 *
 * 为什么需要：原来的实现【假设电位器能打到 0~4095 满量程】来归一化，
 * 但廉价摇杆模块的电位器实际常常只有 400~3600 左右。结果是推到底也只能
 * 输出 ±70% 左右，映射出来的方向判定余量不足、手感发虚。
 *
 * 做法：记录每个轴实际到达过的极值，用 (center, min, max) 三点归一化，
 *      这样推到底就是 ±100%。
 *
 * 两个细节：
 *   1. 初值取【满量程】而不是窄范围 —— 开始时偏保守，随使用逐渐收敛到真实行程，
 *      绝不会出现"刚开始动一点点就满输出"的危险情况
 *   2. 需要连续 TRAVEL_CONFIRM 次越过当前极值才更新，避免 ADC 单次脉冲把量程撑坏
 *      （极值只会向两侧扩展，一旦被噪声撑大就回不来了）
 */
#define TRAVEL_CONFIRM  4

static uint16_t s_min[AXIS_COUNT]     = { 0, 0, 0, 0 };
static uint16_t s_max[AXIS_COUNT]     = { 4095, 4095, 4095, 4095 };
static uint8_t  s_min_cnt[AXIS_COUNT] = { 0, 0, 0, 0 };
static uint8_t  s_max_cnt[AXIS_COUNT] = { 0, 0, 0, 0 };

/* 校准期间允许的最大读数跨度。超过说明该轴悬空或接线不良。
   正常电位器静止时 ADC 抖动通常 < 100。 */
#define STICK_CAL_SPREAD_MAX  300

static uint16_t s_last_good[AXIS_COUNT] = { 2048, 2048, 2048, 2048 };
static uint32_t s_adc_err[AXIS_COUNT]   = { 0, 0, 0, 0 };

static esp_err_t adc_init(void)
{
    const adc_oneshot_unit_init_cfg_t unit_cfg = {
        .unit_id  = ADC_UNIT_1,
        .ulp_mode = ADC_ULP_MODE_DISABLE,
    };
    esp_err_t err = adc_oneshot_new_unit(&unit_cfg, &s_adc);
    if (err != ESP_OK) { ESP_LOGE(TAG, "adc_oneshot_new_unit: %s", esp_err_to_name(err)); return err; }

    const adc_oneshot_chan_cfg_t chan_cfg = {
        .atten    = ADC_ATTEN_DB_12,        /* 量程 0 ~ ~3.3V（摇杆已改接 3V3）*/
        .bitwidth = ADC_BITWIDTH_DEFAULT,   /* 12 bit → 0..4095 */
    };
    for (int a = 0; a < AXIS_COUNT; a++) {
        err = adc_oneshot_config_channel(s_adc, s_chan[a], &chan_cfg);
        if (err != ESP_OK) {
            ESP_LOGE(TAG, "adc config %s: %s", s_axis_name[a], esp_err_to_name(err));
            return err;
        }
    }
    return ESP_OK;
}

/**
 * 采 8 次取中位数（插入排序，8 个元素开销极小）
 *
 * ★ 读失败时【沿用上次有效值】，绝不填 0。
 *   之前写成 `if (read != ESP_OK) raw = 0;` —— 这个静默填 0 是致命的：
 *   一旦 ADC 读失败（例如被另一个任务并发访问），校准出来中心就会变成 0，
 *   整个轴永久错乱。
 */
static uint16_t adc_read_median(int axis)
{
    uint16_t v[ADC_SAMPLES_PER_SCAN];
    int n = 0;

    for (int i = 0; i < ADC_SAMPLES_PER_SCAN; i++) {
        int raw = 0;
        if (adc_oneshot_read(s_adc, s_chan[axis], &raw) == ESP_OK) {
            v[n++] = (uint16_t)raw;
        }
    }

    if (n == 0) {
        s_adc_err[axis]++;
        return s_last_good[axis];       /* 全部失败 → 用上次的值 */
    }

    for (int i = 1; i < n; i++) {
        const uint16_t key = v[i];
        int j = i - 1;
        while (j >= 0 && v[j] > key) { v[j + 1] = v[j]; j--; }
        v[j + 1] = key;
    }

    const uint16_t med = v[n / 2];
    s_last_good[axis] = med;
    return med;
}

/** ★ 行程自学习：记录实际到达过的极值（连续 TRAVEL_CONFIRM 次才认）*/
static void travel_track(int axis, uint16_t raw)
{
    if (raw > s_max[axis]) {
        if (++s_max_cnt[axis] >= TRAVEL_CONFIRM) { s_max[axis] = raw; s_max_cnt[axis] = 0; }
    } else {
        s_max_cnt[axis] = 0;
    }

    if (raw < s_min[axis]) {
        if (++s_min_cnt[axis] >= TRAVEL_CONFIRM) { s_min[axis] = raw; s_min_cnt[axis] = 0; }
    } else {
        s_min_cnt[axis] = 0;
    }
}

/** 归一化到 -100..+100，以中心为原点、自学习行程做等比映射，超程截断在 ±100 */
static int8_t normalize(uint16_t raw, int axis)
{
    const int c = (int)s_center[axis];
    const int d = (int)raw - c;

    /* 原始值死区：抗 ADC 噪声，避免静止时读数跳动 */
    if (d > -STICK_DEADBAND_RAW && d < STICK_DEADBAND_RAW) return 0;

    int pct;
    if (d >= 0) {
        const int span = (int)s_max[axis] - c;
        pct = (span > 1) ? (d * 100) / span : 100;
    } else {
        const int span = c - (int)s_min[axis];
        pct = (span > 1) ? (d * 100) / span : -100;
    }

    if (pct >  100) pct =  100;
    if (pct < -100) pct = -100;
    return (int8_t)pct;
}

/**
 * 方向判定：死区 + 迟滞
 *   进入某方向需要 |pct| >= 死区；已经在某方向时，退出需要 |pct| < 死区-4%。
 *   迟滞能彻底消除摇杆停在阈值附近时的方向抖动。
 *   斜向：X、Y 两轴【各自独立】判阈，两轴同时越阈自然产生斜向，无需特殊处理。
 */
static uint8_t dirs_update(int stick, int8_t x, int8_t y, uint8_t prev)
{
    const int dz  = s_sp[stick].deadzone_pct;
    const int hys = (dz > STICK_HYSTERESIS_PCT) ? dz - STICK_HYSTERESIS_PCT : 0;
    uint8_t dirs = 0;

    /* X 轴：值为正 = 向右（invert_x 把方向反过来）*/
    const int xv = s_sp[stick].invert_x ? -(int)x : (int)x;
    if (xv >=  ((prev & DIRBIT_RIGHT) ? hys : dz)) dirs |= DIRBIT_RIGHT;
    if (xv <= -((prev & DIRBIT_LEFT)  ? hys : dz)) dirs |= DIRBIT_LEFT;

    /* Y 轴：约定「原始值增大 = 向下」。实测相反就用 invert_y。 */
    const int yv = s_sp[stick].invert_y ? -(int)y : (int)y;
    if (yv >=  ((prev & DIRBIT_DOWN) ? hys : dz)) dirs |= DIRBIT_DOWN;
    if (yv <= -((prev & DIRBIT_UP)   ? hys : dz)) dirs |= DIRBIT_UP;

    return dirs;
}

/** 摇杆中心自校准：采 STICK_CENTER_SAMPLES 次求平均，并统计波动范围用于诊断 */
static void stick_calibrate(void)
{
    uint32_t sum[AXIS_COUNT] = { 0, 0, 0, 0 };
    uint16_t mn[AXIS_COUNT], mx[AXIS_COUNT];
    for (int a = 0; a < AXIS_COUNT; a++) { mn[a] = 4095; mx[a] = 0; }

    for (int i = 0; i < STICK_CENTER_SAMPLES; i++) {
        for (int a = 0; a < AXIS_COUNT; a++) {
            const uint16_t v = adc_read_median(a);
            sum[a] += v;
            if (v < mn[a]) mn[a] = v;
            if (v > mx[a]) mx[a] = v;
        }
        vTaskDelay(pdMS_TO_TICKS(2));
    }

    for (int a = 0; a < AXIS_COUNT; a++) {
        const uint16_t prev = s_center[a];
        const uint16_t calc = (uint16_t)(sum[a] / STICK_CENTER_SAMPLES);

        /* ★ 合理性检查：中心落在 200~3900 之外必然不对（正常机械中位在量程中部附近）。
           校准失败时【沿用上次的值】，绝不接受一个会把整个轴搞乱的中心。
           之前就是因为 ADC 读失败被静默填 0，把中心校成了 0。 */
        if (calc < 200 || calc > 3900) {
            s_center[a] = prev;
            ESP_LOGE(TAG, "%s 中心校准结果 %u 不合理（ADC 读失败或摇杆被推住）—— 沿用上次值 %u",
                     s_axis_name[a], calc, prev);
            s_axis_fault |= (uint8_t)(1u << a);
        } else {
            s_center[a] = calc;
        }
    }

    /* ── 逐轴健康判定 ──
       静止的电位器读数应该稳定且落在量程中部附近。
       跨度极大 → 引脚悬空 / 接线松；中心贴轨 → 短路或断路。
       ★ 判定为故障的轴会被【永久禁用】，绝不输出方向键 ——
         宁可少一个轴，也不能让悬空引脚随机乱按键盘。
       ★ 右摇杆还没接线时，这两条会判定它故障并禁用，这是**正确行为**：
         没接线的轴本来就不该输出方向键。 */
    for (int a = 0; a < AXIS_COUNT; a++) {
        const uint16_t spread = (uint16_t)(mx[a] - mn[a]);
        bool bad = false;

        if (spread > STICK_CAL_SPREAD_MAX) {
            ESP_LOGE(TAG, "%s 读数剧烈跳动（跨度 %u）—— 引脚悬空或接线松脱，该轴已禁用",
                     s_axis_name[a], spread);
            bad = true;
        }
        if (s_center[a] < 100 || s_center[a] > 4000) {
            ESP_LOGE(TAG, "%s 中心值 %u 贴轨 —— 短路/断路或未接线，该轴已禁用",
                     s_axis_name[a], s_center[a]);
            bad = true;
        }
        if (bad) s_axis_fault |= (uint8_t)(1u << a);
    }

    ESP_LOGI(TAG, "摇杆中心自校准完成：");
    for (int a = 0; a < AXIS_COUNT; a++) {
        ESP_LOGI(TAG, "  %s 中心=%u  静止跨度=%u~%u  %s",
                 s_axis_name[a], s_center[a], mn[a], mx[a],
                 (s_axis_fault & (1u << a)) ? "【已禁用】" : "正常");
    }
}

/** 把行程重置为满量程（随后由 travel_track 重新自学习）*/
static void travel_reset(void)
{
    for (int a = 0; a < AXIS_COUNT; a++) {
        s_min[a] = 0;
        s_max[a] = 4095;
        s_min_cnt[a] = 0;
        s_max_cnt[a] = 0;
    }
}

/* ── 校准请求：只置标志，真正的校准在扫描任务里做 ──
 *
 * ★ 为什么必须这样：
 *   CFG_CALIB_RESET 是在 tud_hid_set_report_cb() 里处理的，
 *   也就是【TinyUSB 任务】上下文。而真个校准要连续读几百次 ADC，
 *   同时 Core 0 的扫描任务也在读同一个 adc_oneshot 单元 ——
 *   adc_oneshot 不是线程安全的，并发访问会让底层状态错乱、读失败，
 *   结果中心被校成 0，整个轴永久错乱。实测踩过这个坑。
 */
static volatile bool s_calib_request = false;

static void do_calibration(void)
{
    ESP_LOGW(TAG, "开始重置摇杆校准（中心 + 行程）…");
    travel_reset();
    s_axis_fault = 0;
    for (int a = 0; a < AXIS_COUNT; a++) s_adc_err[a] = 0;
    stick_calibrate();
    ESP_LOGW(TAG, "校准完成。行程已恢复满量程，推到底几次即收敛");
}

void input_scan_request_calibration(void)
{
    s_calib_request = true;     /* 线程安全：由扫描任务消费 */
}

void input_scan_get_adc_errors(uint32_t out[AXIS_COUNT])
{
    for (int a = 0; a < AXIS_COUNT; a++) out[a] = s_adc_err[a];
}

/* ══════════════════════════════════════════════════════════════
 *  数字输入（16 路）
 * ══════════════════════════════════════════════════════════════ */

/** 一路数字输入的「引脚 → 槽位」对应 */
typedef struct {
    int gpio;
    int slot;
} din_t;

/* ★ 顺序无所谓（下标只是数组位置），关键是 gpio→slot 的对应。
   权威数据 hardware_ref/xbox_pcb/pinmap.csv，有 tools/check_pinmap.py 校验。 */
static const din_t s_din[DIGITAL_INPUT_COUNT] = {
    { BTN_GPIO_A,          SLOT_A          },
    { BTN_GPIO_B,          SLOT_B          },
    { BTN_GPIO_X,          SLOT_X          },
    { BTN_GPIO_Y,          SLOT_Y          },
    { BTN_GPIO_LB,         SLOT_LB         },
    { BTN_GPIO_RB,         SLOT_RB         },
    { BTN_GPIO_VIEW,       SLOT_VIEW       },
    { BTN_GPIO_MENU,       SLOT_MENU       },
    { BTN_GPIO_XBOX,       SLOT_XBOX       },
    { BTN_GPIO_SHARE,      SLOT_SHARE      },
    { BTN_GPIO_L3,         SLOT_L3         },
    { BTN_GPIO_R3,         SLOT_R3         },
    { BTN_GPIO_DPAD_UP,    SLOT_DPAD_UP    },
    { BTN_GPIO_DPAD_DOWN,  SLOT_DPAD_DOWN  },
    { BTN_GPIO_DPAD_LEFT,  SLOT_DPAD_LEFT  },
    { BTN_GPIO_DPAD_RIGHT, SLOT_DPAD_RIGHT },

    /* ★ 扳机：数字量输入。
     *   Xbox 扳机是电位器/霍尔，抽头电压 0 → 3.3V；
     *   而 GPIO40/41 **不是 ADC 脚**（ESP32-S3 只有 ADC1=GPIO1-10、
     *   ADC2=GPIO11-20），数字输入会在 ~1.65V 处翻转 ——
     *   即**行程过半判为按下**。键盘映射只需要这个二值信号，够用。 */
    { BTN_GPIO_LT,         SLOT_LT         },
    { BTN_GPIO_RT,         SLOT_RT         },
};

static uint8_t s_din_counter[DIGITAL_INPUT_COUNT];
static bool    s_din_stable[DIGITAL_INPUT_COUNT];

static esp_err_t din_init(void)
{
    uint64_t mask = 0;
    for (int i = 0; i < DIGITAL_INPUT_COUNT; i++) mask |= (1ULL << s_din[i].gpio);

    const gpio_config_t cfg = {
        .pin_bit_mask = mask,
        .mode         = GPIO_MODE_INPUT,
        .pull_up_en   = GPIO_PULLUP_ENABLE,     /* 按下接地 → 需要上拉 */
        .pull_down_en = GPIO_PULLDOWN_DISABLE,
        .intr_type    = GPIO_INTR_DISABLE,
    };
    const esp_err_t err = gpio_config(&cfg);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "gpio_config 失败: %s", esp_err_to_name(err));
        return err;
    }

    /* ★★ 开机自检：哪些引脚**现在就读到低电平**
     *
     * 为什么值得专门做一遍：所有数字输入都是"上拉 + 按下接地"，
     * 也就是说**没接线的脚必须读高**。开机就见到低电平只有三种可能：
     *   1. 你正按着那个键（正常）
     *   2. 焊到了开关的**公共端**那侧（那侧本来就接地）—— 于是这个键永远"按住"，
     *      它映射的键盘键会被一直按住，电脑上根本没法打字
     *   3. 有锡桥 / 引脚被短到地
     *
     * 这三种都会表现成"某个键没反应或者一直按着"，靠万用表一根根查很费时间；
     * 固件直接把引脚号打出来，一眼就能定位。
     * （实测踩过：RB 焊到了公共端，GPIO11 常低 → 电脑上 Q 键被按住。） */
    int lowcnt = 0;
    for (int i = 0; i < DIGITAL_INPUT_COUNT; i++) {
        if (gpio_get_level(s_din[i].gpio) != 0) continue;
        lowcnt++;
        ESP_LOGW(TAG, "⚠ 开机即读到低电平：GPIO%d（槽位 %d）—— "
                      "若此时没按键，说明焊到了公共端/有短路，该键会一直处于按下状态",
                 s_din[i].gpio, s_din[i].slot);
    }
    if (lowcnt == 0) {
        ESP_LOGI(TAG, "数字输入自检：%d 路全部读高（未按下）—— 无短路迹象",
                 DIGITAL_INPUT_COUNT);
    } else {
        ESP_LOGW(TAG, "数字输入自检：%d/%d 路读到低电平（见上）",
                 lowcnt, DIGITAL_INPUT_COUNT);
    }

    return ESP_OK;
}

/** 计数防抖：连续 KEY_DEBOUNCE_COUNT 次电平一致才翻转稳定值（= 5ms 窗口）*/
static void din_update(void)
{
    for (int i = 0; i < DIGITAL_INPUT_COUNT; i++) {
        const bool pressed = (gpio_get_level(s_din[i].gpio) == 0);   /* 低电平 = 按下 */

        if (pressed != s_din_stable[i]) {
            if (++s_din_counter[i] >= KEY_DEBOUNCE_COUNT) {
                s_din_stable[i]  = pressed;
                s_din_counter[i] = 0;
            }
        } else {
            s_din_counter[i] = 0;
        }
    }
}

/**
 * 诊断：数字输入的原始电平（未防抖），4 字节 = 26 个槽位。
 *
 * ★★ 这里**只打包、不打日志**。
 *   原来这里跟着一句 ESP_LOGI，而它是在 1ms 扫描循环里调的 ——
 *   一行 60 字符在 115200 下要写 **5ms**，等于每次按键都把扫描任务卡住 5ms。
 *   实测就是靠"扫描超时"告警抓到的（本 tick 用了 5 ms）。
 *
 *   日志本身很有用（"bit 变了 = 引脚收到了信号，问题在软件"），
 *   所以搬到 main.c 的 1 秒状态任务里去打 —— 那里不在热循环里，
 *   慢一点无所谓。原始电平也照样随每帧 Input Report 上报给上位机。
 */
static void din_pack_raw(uint8_t out[4])
{
    uint32_t raw = 0;
    for (int i = 0; i < DIGITAL_INPUT_COUNT; i++) {
        if (gpio_get_level(s_din[i].gpio) == 0) raw |= (1u << s_din[i].slot);
    }
    out[0] = (uint8_t)(raw & 0xFF);
    out[1] = (uint8_t)((raw >> 8) & 0xFF);
    out[2] = (uint8_t)((raw >> 16) & 0xFF);
    out[3] = (uint8_t)((raw >> 24) & 0xFF);
}

/** 最近一次打包的原始电平（供状态任务比较是否变化）*/
static uint8_t s_din_raw[4] = { 0, 0, 0, 0 };

/* ══════════════════════════════════════════════════════════════
 *  扫描任务
 * ══════════════════════════════════════════════════════════════ */

static void scan_task(void *arg)
{
    (void)arg;
    TickType_t last_wake = xTaskGetTickCount();

    /* ★★ 轮询下标：每 tick 只采 **1 个轴**
     *
     * 为什么必须这样（实测踩过，设备直接起不来）：
     *   本任务优先级 10，main_task 只有 1。原来每 tick 采 2 轴 × 8 次 = 16 次 ADC 读，
     *   勉强在 1ms 内；改成 4 轴之后是 32 次读，**超过 1ms** ——
     *   于是 vTaskDelayUntil 发现已经过了截止时间，立刻返回不再让出 CPU，
     *   扫描任务把 main_task 活活饿死，tinyusb_driver_install() 根本没机会执行。
     *   表现是：日志停在"输入采集初始化完成"，原生 USB 口退回 USB-Serial-JTAG。
     *
     *   改成轮询后每 tick 只做 8 次 ADC 读（≈200µs），1ms 预算绰绰有余；
     *   每个轴 4ms 更新一次 = 250Hz，而上报才 20Hz，完全够用。
     */
    int rr = 0;
    uint32_t overruns = 0;

    uint16_t raw[AXIS_COUNT] = { 0, 0, 0, 0 };
    int8_t   pct[AXIS_COUNT] = { 0, 0, 0, 0 };

    while (1) {
        const TickType_t work_start = xTaskGetTickCount();

        /* ── 校准请求：由上位机命令置标志，在这里执行 ──
           ★ 绝不能在 USB 回调里直接做校准：那会和本任务并发访问同一个 ADC 单元，
             adc_oneshot 不是线程安全的，读失败会导致中心被校成 0。*/
        if (s_calib_request) {
            s_calib_request = false;
            do_calibration();
        }

        /* ── 本 tick 只采这一个轴 ── */
        raw[rr] = adc_read_median(rr);

        /* ★ 行程自学习：故障轴不学，免得把悬空的垃圾值当成极值 */
        if (!(s_axis_fault & (1u << rr))) {
            travel_track(rr, raw[rr]);
            pct[rr] = normalize(raw[rr], rr);
        } else {
            pct[rr] = 0;
        }

        /* ── 数字输入每 tick 都扫（纯 GPIO 读，开销极小）──
         * ★ 这里绝对不能打日志：串口写一行要几毫秒，会把 1ms 预算撑爆。 */
        din_update();
        din_pack_raw(s_din_raw);

        const bool full_round = (rr == AXIS_COUNT - 1);
        rr = (rr + 1) % AXIS_COUNT;

        /* ── 4 个轴都刷新过之后才推导方向并发布状态 ──
         *   否则会拿上上轮的轴向值去算方向，出现"方向比摇杆慢一拍"的怪现象。 */
        if (full_round) {
            uint32_t slots = 0;

            for (int s = 0; s < STICK_COUNT; s++) {
                const int ax = s * 2, ay = s * 2 + 1;
                uint8_t d = 0;

                /* 两轴都故障才整组禁用；单轴故障只丢那一个方向 */
                if (!(s_axis_fault & (1u << ax)) || !(s_axis_fault & (1u << ay))) {
                    d = dirs_update(s, pct[ax], pct[ay], s_dir_bits[s]);
                }
                s_dir_bits[s] = d;

                const int base = (s == STICK_L) ? SLOT_LS_UP : SLOT_RS_UP;
                if (d & DIRBIT_UP)    slots |= (1u << (base + 0));
                if (d & DIRBIT_DOWN)  slots |= (1u << (base + 1));
                if (d & DIRBIT_LEFT)  slots |= (1u << (base + 2));
                if (d & DIRBIT_RIGHT) slots |= (1u << (base + 3));
            }

            /* 16 路数字输入（含十字键，它们直接就是槽位 12..15）*/
            for (int i = 0; i < DIGITAL_INPUT_COUNT; i++) {
                if (s_din_stable[i]) slots |= (1u << s_din[i].slot);
            }

            /* ── 发布状态（整体拷贝，临界区极短）── */
            portENTER_CRITICAL(&s_mux);
            s_state.slots = slots;
            memcpy(s_state.btn_raw, s_din_raw, 4);
            for (int a = 0; a < AXIS_COUNT; a++) {
                s_state.raw[a]    = raw[a];
                s_state.center[a] = s_center[a];
                s_state.tmin[a]   = s_min[a];
                s_state.tmax[a]   = s_max[a];
                s_state.pct[a]    = pct[a];
            }
            s_state.uptime_ms  = (uint32_t)(esp_timer_get_time() / 1000);
            s_state.axis_fault = s_axis_fault;
            s_state.seq++;
            if (s_axis_fault) s_state.flags |= INP_FLAG_STICK_FAULT;
            else              s_state.flags &= (uint8_t)~INP_FLAG_STICK_FAULT;
            portEXIT_CRITICAL(&s_mux);
        }

        /* ★★ 超时告警 —— 本任务优先级 10，main_task 只有 1，TinyUSB 任务 5。
         *
         * 一旦本 tick 的工作超过 1ms，vTaskDelayUntil 发现已经过了截止时间就
         * **立刻返回、不再让出 CPU**，于是低优先级任务被活活饿死：
         *   表现是"日志停在半路、原生 USB 口退回 USB-Serial-JTAG、设备起不来"，
         *   查起来要翻半天。
         * 加了这条告警，同一个问题会在日志里直接说是"扫描超时"。
         *
         * 实测：4 轴 × 8 次 ADC 读 = 32 次/ms 就会超；改成每 tick 轮询 1 个轴
         * （8 次读 ≈ 200µs）之后稳定不超。
         */
        const TickType_t used = xTaskGetTickCount() - work_start;
        if (used > pdMS_TO_TICKS(SCAN_PERIOD_MS)) {
            overruns++;
            if (overruns == 1 || overruns % 2000 == 0) {
                ESP_LOGW(TAG, "★ 扫描超时：本 tick 用了 %u ms（预算 %d ms），累计 %lu 次 —— "
                              "任务过重会把 main_task / TinyUSB 饿死",
                         (unsigned)(used * 1000 / configTICK_RATE_HZ),
                         SCAN_PERIOD_MS, (unsigned long)overruns);
            }
        }

        vTaskDelayUntil(&last_wake, pdMS_TO_TICKS(SCAN_PERIOD_MS));
    }
}

/* ══════════════════════════════════════════════════════════════
 *  对外接口
 * ══════════════════════════════════════════════════════════════ */

void input_scan_set_mounted(bool mounted)
{
    portENTER_CRITICAL(&s_mux);
    if (mounted) s_state.flags |=  INP_FLAG_MOUNTED;
    else         s_state.flags &= (uint8_t)~INP_FLAG_MOUNTED;
    portEXIT_CRITICAL(&s_mux);
}

void input_scan_get_state(input_state_t *out)
{
    portENTER_CRITICAL(&s_mux);
    *out = s_state;
    portEXIT_CRITICAL(&s_mux);
}

esp_err_t input_scan_init(void)
{
    memset(&s_state, 0, sizeof(s_state));

    esp_err_t err = adc_init();
    if (err != ESP_OK) return err;

    err = din_init();
    if (err != ESP_OK) return err;

    stick_calibrate();

    s_state.flags |= INP_FLAG_VALID;
    ESP_LOGI(TAG, "输入采集初始化完成：%d 路数字输入 + %d 个摇杆（%d 路 ADC）",
             DIGITAL_INPUT_COUNT,
             STICK_COUNT, AXIS_COUNT);
    ESP_LOGI(TAG, "行程自学习已启用：初值取满量程 (0~4095)，推到底几次后会收敛到实际行程");
    return ESP_OK;
}

esp_err_t input_scan_start(void)
{
    /* 防御性检查：1ms 周期要求 FreeRTOS 时基 ≥ 1000Hz。
       若 CONFIG_FREERTOS_HZ=100，pdMS_TO_TICKS(1) 会被整数除成 0，
       xTaskDelayUntil 会因 xTimeIncrement==0 直接断言失败并无限重启。 */
    if (pdMS_TO_TICKS(SCAN_PERIOD_MS) == 0) {
        ESP_LOGE(TAG, "pdMS_TO_TICKS(%d) == 0 —— 请把 CONFIG_FREERTOS_HZ 设为 1000（当前时基太慢）",
                 SCAN_PERIOD_MS);
        return ESP_ERR_INVALID_ARG;
    }

    const BaseType_t ok = xTaskCreatePinnedToCore(
        scan_task, "input_scan", 4096, NULL, 10, NULL, 0);   /* Core 0，高优先级 */
    if (ok != pdPASS) {
        ESP_LOGE(TAG, "创建扫描任务失败");
        return ESP_ERR_NO_MEM;
    }
    ESP_LOGI(TAG, "扫描任务已启动（%dms 周期，Core 0，时基 %dHz）",
             SCAN_PERIOD_MS, configTICK_RATE_HZ);
    return ESP_OK;
}
