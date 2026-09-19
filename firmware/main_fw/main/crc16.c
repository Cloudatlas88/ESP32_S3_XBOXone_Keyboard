/**
 * @file crc16.c
 * @brief CRC-16/CCITT-FALSE —— 固件与上位机共用同一算法
 *
 * 多项式 0x1021，初值 0xFFFF，不反转，不异或输出。
 * 上位机 C# 侧必须用同一份实现（含同一组测试向量）。
 */
#include "crc16.h"

uint16_t crc16_ccitt(const void *data, size_t len)
{
    const uint8_t *p = (const uint8_t *)data;
    uint16_t crc = 0xFFFF;

    for (size_t i = 0; i < len; i++) {
        crc ^= (uint16_t)p[i] << 8;
        for (int b = 0; b < 8; b++) {
            crc = (crc & 0x8000) ? (uint16_t)((crc << 1) ^ 0x1021)
                                 : (uint16_t)(crc << 1);
        }
    }
    return crc;
}
