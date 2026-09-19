/**
 * @file usb_descriptors.h
 * @brief USB 描述符对外声明
 */
#pragma once

#include <stdint.h>
#include "tusb.h"

#ifdef __cplusplus
extern "C" {
#endif

/** 设备描述符（18 字节）。iSerialNumber = 0 —— 不报告序列号。 */
extern const tusb_desc_device_t desc_device;

/** 配置描述符（含 2 个接口各自的 HID 描述符与端点描述符）。 */
extern const uint8_t desc_configuration[];

/** 字符串描述符表。索引 0 = 语言 ID。 */
extern const char *desc_string_table[];
extern const int   desc_string_count;

/** 接口 0：标准 HID 引导键盘报告描述符（8 字节输入 + 1 字节 LED 输出）。 */
extern const uint8_t hid_report_desc_keyboard[];

/** 接口 1：厂商自定义报告描述符（Feature Report，64 字节含 ID）。 */
extern const uint8_t hid_report_desc_vendor[];

#ifdef __cplusplus
}
#endif
