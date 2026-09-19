/**
 * @file hid_config.h
 * @brief 厂商接口 Feature Report 配置协议
 *
 * 帧格式（Report ID + 63 字节负载）：
 *
 *   负载偏移  字段    长度  说明
 *   0         CMD     1     命令码
 *   1         SEQ     1     包序号，从 0 递增
 *   2         FLAGS   1     bit0 = LAST（最后一包）
 *   3-4       OFF     2 LE  本包数据在完整配置中的字节偏移
 *   5-6       TOTAL   2 LE  完整配置总长度
 *   7-8       CRC16   2 LE  仅最后一包有效：整个配置的 CRC16-CCITT
 *   9-62      DATA    54    负载
 *
 * 交互模型是「先 SET 再 GET」：
 *   上位机用 SET_REPORT 下达命令（带参数），随后用 GET_REPORT 取回应答。
 *   Feature Report 走 EP0 控制传输，天然可靠、不会丢包。
 *
 * ⚠️ TinyUSB 约定：get/set 回调收到的 buffer 【不含 Report ID】，
 *    返回值也只是【负载长度】。见 tinyusb hid_device.c 的
 *    HID_REQ_CONTROL_GET_REPORT / SET_REPORT 分支。
 */
#pragma once

#include <stdint.h>
#include <stdbool.h>
#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

/* ── 命令码 ── */
#define KB_CMD_GET_INFO   0x01
#define KB_CMD_CFG_READ   0x02
#define KB_CMD_CFG_WRITE  0x03
#define KB_CMD_CFG_SAVE   0x04
#define KB_CMD_CFG_RESET  0x05
#define KB_CMD_CALIB_RESET 0x06   /* 重置摇杆行程校准（调用时摇杆须静止）*/
/* 0x07 原来是「切换宏总开关」。总开关功能已移除，这个命令码废弃不用 ——
   故意【不复用】它，免得新旧固件/上位机混用时把某个不相关的动作触发出来。 */
#define KB_CMD_MACRO_RUN  0x08    /* 触发/开关宏：DATA[0] = 宏索引；0xFF = 中止当前 */
#define KB_CMD_STATUS     0x09    /* 只读查询运行状态（不改任何东西，见下方 DATA 布局）*/
#define KB_CMD_REBOOT     0x0A    /* 软重启设备：DATA[0..1] = 延迟毫秒（0 = 默认 800）*/

/* KB_CMD_REBOOT 应答的 DATA 布局 */
#define KB_REBOOT_DELAY   1       /* u16 实际采用的重启延迟（ms）*/

/* KB_CMD_STATUS 应答的 DATA 布局。
   ⚠️ DATA[0] 被 resp_ack() 占用（回填原命令码），所以数据从 DATA[1] 开始。
   ⚠️ DATA[1] 原来是「宏总开关状态」，总开关移除后改为保留（恒为 0）——
      位置保留不动，是为了不让后面的字段整体搬家。 */
#define KB_STAT_RESERVED  1       /* 保留（原宏总开关状态，恒为 0）*/
#define KB_STAT_BUSY      2       /* 1 = 有宏在执行 */
#define KB_STAT_CUR_MACRO 3       /* 当前宏索引；0xFF = 空闲 */
#define KB_STAT_RUN_CNT   4       /* u32 累计触发次数 */
#define KB_STAT_ABORT_CNT 8       /* u32 累计中止/超时次数 */
#define KB_STAT_STEP_CNT  12      /* u32 累计执行步数 */
#define KB_STAT_MODIFIERS 16      /* ★ 当前键盘报告里的修饰键字节 */
#define KB_STAT_PRESSED   17      /* ★ 当前按下的键数 */

#define KB_CMD_ACK        0x80
#define KB_CMD_NACK       0x81

/* ── 错误码（NACK 的 DATA[1]）── */
#define KB_ERR_OK         0x00
#define KB_ERR_UNKNOWN    0x01   /* 未知命令 */
#define KB_ERR_PARAM      0x02   /* 参数错误 */
#define KB_ERR_CRC        0x03   /* CRC 校验失败 */
#define KB_ERR_TOO_LONG   0x04   /* 长度超限 */
#define KB_ERR_BUSY       0x05   /* 暂存区忙 */
#define KB_ERR_TOO_OFTEN  0x0A   /* NVS 写入过于频繁 */
#define KB_ERR_NVS        0x0B   /* NVS 写入失败 */
#define KB_ERR_VERSION    0x0C   /* 版本不兼容 */

/* ── 帧内偏移 ── */
#define KB_PKT_CMD        0
#define KB_PKT_SEQ        1
#define KB_PKT_FLAGS      2
#define KB_PKT_OFF        3
#define KB_PKT_TOTAL      5
#define KB_PKT_CRC        7
#define KB_PKT_DATA       9
#define KB_PKT_DATA_MAX   54    /* 63 - 9 */

#define KB_PKT_FLAG_LAST  0x01

/** 初始化（清空暂存缓冲/应答缓冲）*/
esp_err_t hid_config_init(void);

/**
 * 由 tud_hid_get_report_cb 调用。
 * @param report_id 报告 ID（必须等于 KB_CFG_REPORT_ID）
 * @param buffer    输出缓冲区（不含 Report ID）
 * @param reqlen    可写长度
 * @return 写入的负载长度；0 表示设备不支持（TinyUSB 会 STALL）
 */
uint16_t hid_config_get_report(uint8_t report_id, uint8_t *buffer, uint16_t reqlen);

/**
 * 由 tud_hid_set_report_cb 调用。
 * @param report_id 报告 ID
 * @param buffer    输入数据（不含 Report ID）
 * @param bufsize   负载长度
 */
void hid_config_set_report(uint8_t report_id, const uint8_t *buffer, uint16_t bufsize);

/** 累计处理的 SET 命令数（诊断）*/
uint32_t hid_config_set_count(void);

/** 累计处理的 GET 请求数（诊断）*/
uint32_t hid_config_get_count(void);

#ifdef __cplusplus
}
#endif
