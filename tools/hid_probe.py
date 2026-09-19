#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HID 配置通道探针 —— 验证上位机能否打开厂商接口并读写 Feature Report

背景：Windows 对键盘顶层集合 (Usage Page 0x0001 / Usage 0x0006) 是【独占】认领的，
      所以 hidapi 打不开键盘接口。设备把配置通道放在【独立的厂商集合】里
      (Usage Page 0xFF00)，hidapi 才能自由访问。

用法:
    python tools/hid_probe.py
    python tools/hid_probe.py --vid 0x3554 --pid 0xFA09
"""
import argparse
import sys

import hid

CFG_REPORT_ID = 0x03
CFG_REPORT_SIZE = 64


def main() -> int:
    ap = argparse.ArgumentParser(description="HID 配置通道探针")
    ap.add_argument("--vid", type=lambda s: int(s, 0), default=0x3554)
    ap.add_argument("--pid", type=lambda s: int(s, 0), default=0xFA09)
    args = ap.parse_args()

    print(f"=== 枚举 VID:0x{args.vid:04X} PID:0x{args.pid:04X} 的全部 HID 接口 ===")
    devices = hid.enumerate(args.vid, args.pid)
    if not devices:
        print("  ✗ 未找到设备")
        return 1

    for d in devices:
        print(f"  if={d['interface_number']:<3} "
              f"usage_page=0x{d['usage_page']:04X} usage=0x{d['usage']:04X}  "
              f"product={d['product_string']!r}")

    # ★ 只认 Usage Page 0xFF00 —— 那是我们自己定义的厂商集合。
    #   真实接收器的 MI_01 用的是 FF02 / FF04，不会混淆。
    cand = [d for d in devices if d["usage_page"] == 0xFF00]
    if not cand:
        print("\n  ✗ 没有找到 Usage Page 0xFF00 的厂商集合")
        return 1

    print(f"\n=== 打开厂商集合（Usage Page 0xFF00）===")
    # 必须用 path 打开，不能用 vid/pid：
    # hid.Open(vid,pid) 只会打开设备的第一个接口，而那是被 kbdhid 独占的键盘接口，必然失败。
    path = cand[0]["path"]
    if hasattr(hid, "Device"):                  # 新版 API
        dev = hid.Device(path=path)
    else:                                       # 经典 API
        dev = hid.device()
        dev.open_path(path)
    print(f"  ✔ 已打开（说明厂商接口未被系统独占，hidapi 可自由访问）")
    try:
        print(f"    厂商 : {dev.get_manufacturer_string()!r}")
        print(f"    产品 : {dev.get_product_string()!r}")
        print(f"    序列号: {dev.get_serial_number_string()!r}   ← 应为 None（本设备不报告序列号）")
    except Exception as e:      # 不同 hidapi 版本属性名不一致，取不到不影响主流程
        print(f"    (字符串描述符读取跳过: {e})")

    print(f"\n=== 读取 Feature Report (Report ID = 0x{CFG_REPORT_ID:02X}) ===")
    data = dev.get_feature_report(CFG_REPORT_ID, CFG_REPORT_SIZE)
    print(f"  收到 {len(data)} 字节:")
    print("    " + " ".join(f"{b:02X}" for b in data))

    ok = (len(data) == CFG_REPORT_SIZE
          and data[0] == CFG_REPORT_ID     # hidapi 把 Report ID 放在 byte 0
          and data[1] == 0x01              # 协议版本
          and data[2] == 0x01              # 接口号
          and data[3] == 0xCA and data[4] == 0xFE)   # 握手标记

    if ok:
        print(f"\n  ✔ 握手成功：Report ID=0x{data[0]:02X}  协议版本=0x{data[1]:02X}  "
              f"接口={data[2]}  标记=CAFE")
    else:
        print("\n  ✗ 握手数据不符合预期")

    dev.close()
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
