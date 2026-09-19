#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
按设备分组对比 HID 接口 —— 用于确认「我们的 ESP32」与「真实 2.4G 接收器」
在系统和上位机眼里是否完全一致。

用法: python tools/hid_compare.py
"""
import sys

import hid

VID, PID = 0x3554, 0xFA09

# ESP32 的两个接口实例号（来自设备管理器实测）
ESP32_INSTANCES = ("8&2ba13b8f", "8&13ddaf4a")


def main() -> int:
    devs = hid.enumerate(VID, PID)
    if not devs:
        print("✗ 未找到设备")
        return 1

    groups = {"ESP32（我们的设备）": [], "真实 2.4G 接收器": []}
    for d in devs:
        raw = d["path"]
        p = raw.decode(errors="ignore").lower() if isinstance(raw, (bytes, bytearray)) else raw.lower()
        key = "ESP32（我们的设备）" if any(i in p for i in ESP32_INSTANCES) else "真实 2.4G 接收器"
        groups[key].append(d)

    for name, items in groups.items():
        print(f"── {name}：{len(items)} 个 HID 顶层集合")
        if not items:
            print("     (未找到)")
        for d in sorted(items, key=lambda x: (x["interface_number"], x["usage_page"])):
            print(f"     if={d['interface_number']:<3} "
                  f"UP=0x{d['usage_page']:04X} U=0x{d['usage']:04X}   "
                  f"product={d['product_string']!r}  manufacturer={d['manufacturer_string']!r}  "
                  f"serial={d['serial_number']!r}")
        print()

    # 关键对比：接口字符串是否一致
    esp_prods = {d["product_string"] for d in groups["ESP32（我们的设备）"]}
    real_prods = {d["product_string"] for d in groups["真实 2.4G 接收器"] if d["interface_number"] == 0}
    print("── 对比结论")
    print(f"   ESP32 的接口显示名 : {esp_prods}")
    print(f"   真实设备的接口显示名: {real_prods}")
    print(f"   → {'✔ 完全一致' if esp_prods and esp_prods == real_prods else '✗ 不一致'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
