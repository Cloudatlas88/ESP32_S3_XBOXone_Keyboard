/**
 * @file crc16.h
 * @brief CRC-16/CCITT-FALSE
 */
#pragma once

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/**
 * 计算 CRC-16/CCITT-FALSE
 *
 * 测试向量（两端都必须过）：
 *   crc16_ccitt("123456789", 9) == 0x29B1
 *   crc16_ccitt("", 0)          == 0xFFFF
 */
uint16_t crc16_ccitt(const void *data, size_t len);

#ifdef __cplusplus
}
#endif
