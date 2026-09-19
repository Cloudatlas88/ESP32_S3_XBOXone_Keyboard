/**
 * @file hid_config.c
 * @brief 厂商接口 Feature Report 配置协议实现
 */
#include <string.h>

#include "esp_log.h"
#include "esp_system.h"
#include "esp_timer.h"

#include "app_config.h"
#include "crc16.h"
#include "hid_config.h"
#include "hid_keyboard.h"
#include "input_scan.h"
#include "kb_config.h"
#include "macro_engine.h"
#include "nvs_config.h"

static const char *TAG = "hid_cfg";

/* ── 应答 staging（GET_REPORT 只能同步取走，所以必须先算好）── */
static uint8_t  s_resp[KB_CFG_PAYLOAD_SIZE];
static bool     s_resp_valid;

/* ── 分片接收缓冲 ── */
static uint8_t  s_rx[sizeof(kb_config_t)];
static uint16_t s_rx_total;      /* 本次声明要传的总长 */
static uint16_t s_rx_got;        /* 已收到多少字节 */
static uint16_t s_rx_crc;        /* 最后一包带来的 CRC */
static bool     s_rx_last_seen;

static volatile uint32_t s_set_cnt;
static volatile uint32_t s_get_cnt;

/* ── 软重启定时器 ──
   收到 0x0A 后不立刻重启，而是等一会儿再重启，好让上位机取到 ACK。
   见 KB_CMD_REBOOT 分支里的详细说明。 */
static esp_timer_handle_t s_reboot_timer;

static void reboot_timer_cb(void *arg)
{
    (void)arg;
    esp_restart();      /* 不返回 */
}

/* ══════════════════════════════════════════════════════════════
 *  小工具
 * ══════════════════════════════════════════════════════════════ */

static inline uint16_t rd16(const uint8_t *p) { return (uint16_t)(p[0] | (p[1] << 8)); }
static inline void     wr16(uint8_t *p, uint16_t v) { p[0] = (uint8_t)(v & 0xFF); p[1] = (uint8_t)(v >> 8); }
static inline void     wr32(uint8_t *p, uint32_t v)
{
    p[0] = (uint8_t)(v & 0xFF);
    p[1] = (uint8_t)((v >> 8) & 0xFF);
    p[2] = (uint8_t)((v >> 16) & 0xFF);
    p[3] = (uint8_t)((v >> 24) & 0xFF);
}

/** 起一个应答帧 */
static void resp_begin(uint8_t cmd, uint8_t seq)
{
    memset(s_resp, 0, sizeof(s_resp));
    s_resp[KB_PKT_CMD]   = cmd;
    s_resp[KB_PKT_SEQ]   = seq;
    s_resp[KB_PKT_FLAGS] = KB_PKT_FLAG_LAST;
    s_resp_valid = true;
}

/** 往应答帧的 DATA 区填数据 */
static void resp_data(uint16_t off, const void *src, uint16_t len)
{
    if (off >= KB_PKT_DATA_MAX) return;
    if (off + len > KB_PKT_DATA_MAX) len = (uint16_t)(KB_PKT_DATA_MAX - off);
    memcpy(&s_resp[KB_PKT_DATA + off], src, len);
}

static void resp_ack(uint8_t cmd, uint8_t seq)
{
    resp_begin(KB_CMD_ACK, seq);
    s_resp[KB_PKT_DATA + 0] = cmd;
}

static void resp_nack(uint8_t cmd, uint8_t seq, uint8_t err)
{
    resp_begin(KB_CMD_NACK, seq);
    s_resp[KB_PKT_DATA + 0] = cmd;
    s_resp[KB_PKT_DATA + 1] = err;
}

/* ══════════════════════════════════════════════════════════════
 *  各命令处理
 * ══════════════════════════════════════════════════════════════ */

/** GET_INFO：把设备能力告诉上位机（也是默认应答）*/
static void build_info(void)
{
    resp_begin(KB_CMD_GET_INFO, 0);

    uint8_t d[16] = { 0 };
    d[0] = KB_PROTO_VERSION;                       /* 协议版本 */
    wr16(&d[1], (uint16_t)sizeof(kb_config_t));    /* 配置结构体大小 */
    d[3] = KB_PKT_DATA_MAX;                        /* 单包负载上限 */
    wr16(&d[4], KB_CFG_VERSION);                   /* 配置格式版本 */
    d[6] = FW_VER_MAJOR;                           /* 固件版本（见 app_config.h）*/
    d[7] = FW_VER_MINOR;
    d[8] = FW_VER_PATCH;
    d[9] = (uint8_t)(nvs_config_is_dirty() ? 1 : 0);   /* bit0 = 有未落盘改动 */
    wr16(&d[10], (uint16_t)nvs_config_epoch());

    resp_data(0, d, sizeof(d));
}

/** CFG_READ：按 OFF 取一段配置发回 */
static void build_read(uint8_t seq, uint16_t off)
{
    const uint16_t total = (uint16_t)sizeof(kb_config_t);

    if (off >= total) {
        resp_nack(KB_CMD_CFG_READ, seq, KB_ERR_PARAM);
        return;
    }

    const kb_config_t *cfg = nvs_config_active();
    uint16_t n = (uint16_t)(total - off);
    if (n > KB_PKT_DATA_MAX) n = KB_PKT_DATA_MAX;

    resp_begin(KB_CMD_ACK, seq);
    resp_data(0, (const uint8_t *)cfg + off, n);

    /* 应答帧头里回填 本包偏移 / 总长 / 是否最后一包 */
    wr16(&s_resp[KB_PKT_OFF], off);
    wr16(&s_resp[KB_PKT_TOTAL], total);
    s_resp[KB_PKT_FLAGS] = (off + n >= total) ? KB_PKT_FLAG_LAST : 0;
    if (off + n >= total) {
        wr16(&s_resp[KB_PKT_CRC], crc16_ccitt(cfg, sizeof(*cfg)));
    }
}

/** CFG_WRITE：分片写入暂存区 */
static void handle_write(const uint8_t *p, uint16_t bufsize)
{
    const uint8_t  seq    = p[KB_PKT_SEQ];
    const uint8_t  flags  = p[KB_PKT_FLAGS];
    const uint16_t off    = rd16(&p[KB_PKT_OFF]);
    const uint16_t total  = rd16(&p[KB_PKT_TOTAL]);
    const uint16_t crc    = rd16(&p[KB_PKT_CRC]);

    const uint16_t dlen = (uint16_t)(bufsize - KB_PKT_DATA);
    uint16_t eff = (dlen > KB_PKT_DATA_MAX) ? KB_PKT_DATA_MAX : dlen;

    /* 第一包：校验收到的总长是否与本地结构一致 */
    if (off == 0) {
        s_rx_got       = 0;
        s_rx_last_seen = false;
        if (total != sizeof(kb_config_t)) {
            ESP_LOGE(TAG, "配置长度不符: 上位机 %u / 本地 %u", total, (unsigned)sizeof(kb_config_t));
            resp_nack(KB_CMD_CFG_WRITE, seq, KB_ERR_VERSION);
            return;
        }
        s_rx_total = total;
        memset(s_rx, 0, sizeof(s_rx));
    }

    if (total != s_rx_total) {
        resp_nack(KB_CMD_CFG_WRITE, seq, KB_ERR_TOO_LONG);
        return;
    }
    if (off >= s_rx_total) {
        resp_nack(KB_CMD_CFG_WRITE, seq, KB_ERR_TOO_LONG);
        return;
    }

    /* ★ 关键：上位机通常用固定长度的缓冲区发 Feature Report（这里就是 63 字节），
       最后一包末尾会有填充。所以有效长度必须按【声明的 total】截断，
       不能直接用 bufsize 推算 —— 否则最后一包的 off+eff 会越界，误报长度超限。 */
    if (off + eff > s_rx_total) {
        eff = (uint16_t)(s_rx_total - off);
    }

    memcpy(&s_rx[off], &p[KB_PKT_DATA], eff);
    if (off + eff > s_rx_got) s_rx_got = (uint16_t)(off + eff);

    if (flags & KB_PKT_FLAG_LAST) {
        s_rx_last_seen = true;
        s_rx_crc       = crc;

        if (s_rx_got < s_rx_total) {
            resp_nack(KB_CMD_CFG_WRITE, seq, KB_ERR_TOO_LONG);
            return;
        }

        /* 整包 CRC 校验 */
        const uint16_t calc = crc16_ccitt(s_rx, s_rx_total);
        if (calc != crc) {
            ESP_LOGE(TAG, "配置 CRC 不符: 收到 0x%04X / 算出 0x%04X", crc, calc);
            resp_nack(KB_CMD_CFG_WRITE, seq, KB_ERR_CRC);
            return;
        }

        /* 拷进暂存区（尚未生效）*/
        kb_config_t *staging = nvs_config_staging();
        memcpy(staging, s_rx, sizeof(kb_config_t));

        if (!kb_config_validate(staging)) {
            ESP_LOGE(TAG, "暂存区结构校验失败（magic/version/deadzone）");
            resp_nack(KB_CMD_CFG_WRITE, seq, KB_ERR_PARAM);
            return;
        }

        ESP_LOGI(TAG, "收到完整配置 %u 字节，已进暂存区（等待 CFG_SAVE 才生效）", s_rx_total);
    }

    resp_ack(KB_CMD_CFG_WRITE, seq);
}

/** CFG_SAVE：暂存 → 生效 + 立即落盘 */
static void handle_save(uint8_t seq)
{
    const esp_err_t err = nvs_config_commit_staging();
    if (err != ESP_OK) {
        resp_nack(KB_CMD_CFG_SAVE, seq, (err == ESP_ERR_INVALID_CRC) ? KB_ERR_CRC : KB_ERR_PARAM);
        return;
    }

    /* 配额检查：一分钟内写入次数过多才拒绝（防上位机 bug 循环写）。
       正常情况下这里立刻通过，nvs 任务会在 100ms 内完成落盘。 */
    if (!nvs_config_rate_ok()) {
        ESP_LOGW(TAG, "一分钟内写入次数超过配额，本次拒绝落盘（RAM 已生效）");
        resp_nack(KB_CMD_CFG_SAVE, seq, KB_ERR_TOO_OFTEN);
        return;
    }

    resp_ack(KB_CMD_CFG_SAVE, seq);
    s_resp[KB_PKT_DATA + 1] = KB_ERR_OK;
    /* 告诉上位机：落盘是异步的，约 100ms 内完成 —— 提示用户别马上断电 */
    wr16(&s_resp[KB_PKT_DATA + 2], 100);

    ESP_LOGI(TAG, "配置已生效并排队落盘（约 100ms 内写入 NVS）");
}

/* ══════════════════════════════════════════════════════════════
 *  对外接口
 * ══════════════════════════════════════════════════════════════ */

esp_err_t hid_config_init(void)
{
    memset(s_resp, 0, sizeof(s_resp));
    memset(s_rx, 0, sizeof(s_rx));
    s_resp_valid = false;
    s_rx_total = s_rx_got = s_rx_crc = 0;
    s_rx_last_seen = false;

    /* 软重启用的一次性定时器 */
    const esp_timer_create_args_t targs = {
        .callback = reboot_timer_cb,
        .name     = "cfg_reboot",
    };
    if (esp_timer_create(&targs, &s_reboot_timer) != ESP_OK) {
        ESP_LOGE(TAG, "创建重启定时器失败 —— 软重启命令将不可用");
        s_reboot_timer = NULL;
    }

    build_info();       /* 默认应答 = GET_INFO，这样上位机 open 后直接 GET 就能拿到信息 */
    return ESP_OK;
}

uint16_t hid_config_get_report(uint8_t report_id, uint8_t *buffer, uint16_t reqlen)
{
    if (report_id != KB_CFG_REPORT_ID) return 0;
    if (reqlen < KB_CFG_PAYLOAD_SIZE)  return 0;

    if (!s_resp_valid) build_info();    /* 没有待取应答时回落到 GET_INFO */

    s_get_cnt++;
    memcpy(buffer, s_resp, KB_CFG_PAYLOAD_SIZE);

    /* 应答被取走后失效，避免重复读到陈旧数据 */
    s_resp_valid = false;
    return KB_CFG_PAYLOAD_SIZE;
}

void hid_config_set_report(uint8_t report_id, const uint8_t *buffer, uint16_t bufsize)
{
    if (report_id != KB_CFG_REPORT_ID) return;

    s_set_cnt++;

    if (bufsize < KB_PKT_DATA) {
        ESP_LOGW(TAG, "SET 负载太短: %u", bufsize);
        return;
    }

    const uint8_t cmd = buffer[KB_PKT_CMD];
    const uint8_t seq = buffer[KB_PKT_SEQ];

    switch (cmd) {
    case KB_CMD_GET_INFO:
        build_info();
        break;

    case KB_CMD_CFG_READ:
        build_read(seq, rd16(&buffer[KB_PKT_OFF]));
        break;

    case KB_CMD_CFG_WRITE:
        handle_write(buffer, bufsize);
        break;

    case KB_CMD_CFG_SAVE:
        handle_save(seq);
        break;

    case KB_CMD_CFG_RESET:
        nvs_config_factory_reset();
        resp_ack(KB_CMD_CFG_RESET, seq);
        break;

    case KB_CMD_CALIB_RESET:
        /* 重置摇杆校准。
           ★ 这里只置标志：真正的校准由扫描任务执行。
             直接在 USB 回调里读 ADC 会和扫描任务并发访问同一单元，把中心校坏。
             调用时请提醒用户别碰摇杆（中心取自当时的读数）。*/
        input_scan_request_calibration();
        resp_ack(KB_CMD_CALIB_RESET, seq);
        break;

    case KB_CMD_MACRO_RUN: {
        /* 上位机的「测试触发」——不按实体键也能验证宏定义对不对。

           ★ 走 macro_toggle_or_trigger()，和实体按键【完全同一条路】：
             勾了「循环执行」的宏，这里是"开/停切换"；
             非循环宏每次从头跑。所以测试出来的行为就是按键触发的行为。

           应答布局（ACK 与 NACK 不一样，别混）：
             ACK : DATA[0]=1        DATA[1]=保留
                   DATA[2]=本次动作（1=已启动/触发，0=已停止）
                   DATA[3]=这一刻是否有宏在执行
             NACK: DATA[0]=原命令码  DATA[1]=错误码  DATA[2]=保留  DATA[3]=是否在执行

           ★ DATA[2] 必须报【本次动作意图】，不能报"这一刻 is_busy()"：
             宏是"先入队、由宏任务稍后真正启动"的，刚触发那一刻 is_busy() 还是
             false，界面就会把"刚启动"显示成"已停止"（实测踩过）。

           踩过的坑：这里原来在分支之后统一写 DATA[1]，
           把 resp_nack() 刚写进去的错误码覆盖成了 0，上位机永远看不到真实原因。 */
        const uint8_t id = buffer[KB_PKT_DATA + 0];

        if (id == KB_MACRO_RUN_ABORT) {
            macro_abort();
            resp_ack(KB_CMD_MACRO_RUN, seq);
            s_resp[KB_PKT_DATA + 0] = 1;
            s_resp[KB_PKT_DATA + 2] = 0;                    /* 本次动作 = 停止 */
            s_resp[KB_PKT_DATA + 3] = macro_is_busy() ? 1 : 0;
            break;
        }

        bool stopped = false;
        const esp_err_t err = macro_toggle_or_trigger(id, &stopped);

        if (err == ESP_OK) {
            resp_ack(KB_CMD_MACRO_RUN, seq);
            s_resp[KB_PKT_DATA + 0] = 1;
            s_resp[KB_PKT_DATA + 2] = stopped ? 0 : 1;      /* 本次动作 */
            s_resp[KB_PKT_DATA + 3] = macro_is_busy() ? 1 : 0;
        } else {
            resp_nack(KB_CMD_MACRO_RUN, seq, KB_ERR_BUSY);
            s_resp[KB_PKT_DATA + 3] = macro_is_busy() ? 1 : 0;
        }
        break;
    }

    case KB_CMD_STATUS: {
        /* 只读状态查询 —— 专门给工具/上位机轮询用。

           为什么要单独开一条命令：轮询必须【不改动设备状态】。
           早先用 0x07 的 TOGGLE 去旁敲侧击读 busy，结果每查一次就把
           宏总开关翻一次，而关掉总开关会中止正在执行的宏 ——
           "测量宏跑了多久"变成了"每 16ms 把宏掐死一次"。
           走 Feature Report 而不是等 20Hz 的 Input Report，
           是为了拿到 ~2ms 的时间精度（Input Report 有 50ms 量化误差）。 */
        resp_ack(KB_CMD_STATUS, seq);   /* ★ 必须先 ACK：resp_begin 会 memset 整个应答缓冲 */

        /* DATA[KB_STAT_RESERVED] 保持 0（原宏总开关状态，已废弃）*/
        s_resp[KB_PKT_DATA + KB_STAT_BUSY]      = macro_is_busy() ? 1 : 0;
        s_resp[KB_PKT_DATA + KB_STAT_CUR_MACRO] = macro_current();
        wr32(&s_resp[KB_PKT_DATA + KB_STAT_RUN_CNT],   macro_run_count());
        wr32(&s_resp[KB_PKT_DATA + KB_STAT_ABORT_CNT], macro_abort_count());
        wr32(&s_resp[KB_PKT_DATA + KB_STAT_STEP_CNT],  macro_step_count());

        /* ★ 键盘报告的实际状态 —— 给上位机验证"松开之后修饰键有没有真的清掉"。
           没有这两个字节就只能在现场靠"按键会不会莫名变成 Ctrl+X"来发现问题。 */
        s_resp[KB_PKT_DATA + KB_STAT_MODIFIERS] = hid_kb_modifiers();
        s_resp[KB_PKT_DATA + KB_STAT_PRESSED]   = hid_kb_pressed_count();
        break;
    }

    case KB_CMD_REBOOT: {
        /* 软重启 —— 让用户不必拔插 USB 就能验证 NVS 持久化 / 恢复设备。

           ★ 为什么不能直接在这里 esp_restart()：
             本协议是「先 SET 后 GET」两步 —— SET 只是把应答准备好放进 s_resp，
             真正的应答要靠上位机随后那次 GET 来取。
             在这里立刻重启的话，这个 ACK 永远发不出去，
             上位机会看到"命令无应答"，把成功的重启误报成失败。

           所以要延迟重启，给上位机留出取应答的时间（默认 800ms，
           实测上位机取一次应答只要 ~8ms，余量很足）。 */
        uint16_t d = rd16(&buffer[KB_PKT_DATA + 0]);
        if (d < 300)  d = 800;      /* 太短的延迟保证不了上位机取到应答 */
        if (d > 5000) d = 5000;

        resp_ack(KB_CMD_REBOOT, seq);
        wr16(&s_resp[KB_PKT_DATA + KB_REBOOT_DELAY], d);

        /* 复位前先把待落盘配置写完，否则用户刚点的「保存」会丢 */
        const bool flushed = nvs_config_flush_now();
        s_resp[KB_PKT_DATA + KB_REBOOT_DELAY + 2] = flushed ? 1 : 0;

        if (!s_reboot_timer) {
            resp_nack(KB_CMD_REBOOT, seq, KB_ERR_UNKNOWN);
            ESP_LOGE(TAG, "重启定时器不可用，无法软重启");
            break;
        }

        if (esp_timer_start_once(s_reboot_timer, (uint64_t)d * 1000) != ESP_OK) {
            resp_nack(KB_CMD_REBOOT, seq, KB_ERR_BUSY);
            ESP_LOGE(TAG, "启动重启定时器失败");
        } else {
            ESP_LOGW(TAG, "将在 %u ms 后软重启（落盘=%s）", (unsigned)d, flushed ? "OK" : "失败");
        }
        break;
    }

    default:
        ESP_LOGW(TAG, "未知命令 0x%02X", cmd);
        resp_nack(cmd, seq, KB_ERR_UNKNOWN);
        break;
    }
}

uint32_t hid_config_set_count(void) { return s_set_cnt; }
uint32_t hid_config_get_count(void) { return s_get_cnt; }
