#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
输入状态实时查看器（模拟调试界面的命令行版）

读取设备通过厂商接口 Input Report (RID 4) 主动推送的输入状态，
20Hz 上报。这个脚本同时是上位机 C# 界面的「协议参照实现」。

用法:
    python tools/hid_input_view.py                 # 跑 10 秒
    python tools/hid_input_view.py --seconds 30
    python tools/hid_input_view.py --raw           # 每帧都打印（不只在变化时）

负载布局（32 字节，小端）—— 与固件 input_report_task() 严格一致:
    0     flags      1   bit0 数据有效 / bit1 USB已挂载 / bit2 摇杆贴轨故障
    1     buttons    1   bit0..4 = 按钮1..4 + 摇杆按下
    2     dirs       1   bit0 上 / bit1 下 / bit2 左 / bit3 右
    3     x_pct      1   int8  -100..+100
    4     y_pct      1   int8
    5-6   x_raw      2   ADC 原始值 0..4095
    7-8   y_raw      2
    9-10  x_center   2   开机自校准中心
    11-12 y_center   2
    13-16 uptime_ms  4
    17-20 seq        4
    21    axis_fault 1   bit0=X轴故障 bit1=Y轴故障（悬空/贴轨，已禁用）
    22-31 保留       10
"""

import argparse
import sys
import time

import hid

RID_INPUT = 0x04
INPUT_LEN = 32

DIR_NAMES = [(0x01, "上"), (0x02, "下"), (0x04, "左"), (0x08, "右")]
BTN_NAMES = ["按钮1", "按钮2", "按钮3", "按钮4", "摇杆按下"]


def s8(v: int) -> int:
    return v - 256 if v > 127 else v


def decode(p: list) -> dict:
    return {
        "flags":   p[0],
        "buttons": p[1],
        "dirs":    p[2],
        "x_pct":   s8(p[3]),
        "y_pct":   s8(p[4]),
        "x_raw":   p[5] | (p[6] << 8),
        "y_raw":   p[7] | (p[8] << 8),
        "x_ctr":   p[9] | (p[10] << 8),
        "y_ctr":   p[11] | (p[12] << 8),
        "uptime":  p[13] | (p[14] << 8) | (p[15] << 16) | (p[16] << 24),
        "seq":     p[17] | (p[18] << 8) | (p[19] << 16) | (p[20] << 24),
        "fault":   p[21],       # bit0=X轴故障 bit1=Y轴故障
        "x_min":   p[22] | (p[23] << 8),   # ★ 自学习行程
        "x_max":   p[24] | (p[25] << 8),
        "y_min":   p[26] | (p[27] << 8),
        "y_max":   p[28] | (p[29] << 8),
    }


def describe(d: dict) -> str:
    dirs = "".join(n for b, n in DIR_NAMES if d["dirs"] & b) or "-"
    btns = ",".join(n for i, n in enumerate(BTN_NAMES) if d["buttons"] & (1 << i)) or "-"
    warn = ""
    if d["fault"] & 0x01:
        warn += " ★X轴已禁用"
    if d["fault"] & 0x02:
        warn += " ★Y轴已禁用"
    if d["flags"] & 0x04:
        warn += " ★运行期检测到贴轨"
    return (f"X={d['x_raw']:4d}({d['x_pct']:+4d}%)  Y={d['y_raw']:4d}({d['y_pct']:+4d}%)  "
            f"方向={dirs:<4} 按键={btns}{warn}")


def travel_line(d: dict) -> str:
    """行程自学习进度（推到底会逐步收敛到实际行程）"""
    def span(a, b):
        return b - a
    return (f"  行程 X:[{d['x_min']:4d},{d['x_max']:4d}] 跨度{span(d['x_min'], d['x_max']):4d}  "
            f"中心{d['x_ctr']:4d}   |   "
            f"Y:[{d['y_min']:4d},{d['y_max']:4d}] 跨度{span(d['y_min'], d['y_max']):4d}  "
            f"中心{d['y_ctr']:4d}")


def main() -> int:
    ap = argparse.ArgumentParser(description="输入状态实时查看器")
    ap.add_argument("--vid", type=lambda s: int(s, 0), default=0x3554)
    ap.add_argument("--pid", type=lambda s: int(s, 0), default=0xFA09)
    ap.add_argument("--seconds", type=float, default=10.0)
    ap.add_argument("--raw", action="store_true", help="每帧都打印")
    args = ap.parse_args()

    devs = [d for d in hid.enumerate(args.vid, args.pid) if d["usage_page"] == 0xFF00]
    if not devs:
        print("✗ 没找到厂商集合 (Usage Page 0xFF00)")
        return 1

    dev = hid.device()
    dev.open_path(devs[0]["path"])
    dev.set_nonblocking(True)
    print(f"✔ 已连接 {devs[0]['product_string']!r}，开始读取 Input Report (RID 0x{RID_INPUT:02X})…")
    print("  推摇杆 / 按按钮，观察下面的数值变化：")
    print("  " + "-" * 88)

    t_end = time.time() + args.seconds
    last_line = None
    last_d = None
    n = 0

    while time.time() < t_end:
        data = dev.read(64, 200)
        if not data:
            continue
        if data[0] != RID_INPUT:
            continue
        n += 1
        d = decode(data[1:1 + INPUT_LEN])
        last_d = d
        line = describe(d)
        if args.raw or line != last_line:
            print(f"  [{d['seq']:6d}] {line}")
            last_line = line

    print("  " + "-" * 88)
    if last_d is not None:
        print(travel_line(last_d))
    print(f"共收到 {n} 帧（{args.seconds:.0f} 秒，目标 20Hz）")
    dev.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
