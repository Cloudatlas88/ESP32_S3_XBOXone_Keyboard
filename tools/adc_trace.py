#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ADC 原始值追踪 —— 诊断摇杆行程异常

记录每一帧的 x_raw / y_raw / x_pct / y_pct 到 CSV，退出时打印统计分析。

用法:
    python tools/adc_trace.py --seconds 600 --out adc_trace.csv

诊断要点：把摇杆从一端缓慢推到另一端保持不动。
如果 x_raw 随行程【先升后降】（非单调），说明 ADC 读数回折 ——
这不是软件能修的，需要改硬件（分压或换量程）。
"""
import argparse
import sys
import time

import hid

VID, PID = 0x3554, 0xFA09
RID_INPUT = 0x04


def s8(v: int) -> int:
    return v - 256 if v > 127 else v


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--seconds", type=float, default=600)
    ap.add_argument("--out", default="adc_trace.csv")
    args = ap.parse_args()

    cands = [d for d in hid.enumerate(VID, PID) if d["usage_page"] == 0xFF00]
    if not cands:
        print("✗ 找不到厂商配置集合")
        return 1

    dev = hid.device()
    dev.open_path(cands[0]["path"])
    dev.set_nonblocking(True)

    f = open(args.out, "w", encoding="utf-8")
    f.write("t_ms,seq,x_raw,y_raw,x_pct,y_pct,dirs,btn,center_x,center_y,x_min,x_max,y_min,y_max,btn_raw\n")

    n = 0
    t0 = time.time()
    xr = [65535, -1]     # x raw min/max
    yr = [65535, -1]
    xp = [-127, 127]     # x pct min/max
    yp = [-127, 127]
    last_seq = -1

    print(f"开始记录到 {args.out}（{args.seconds:.0f} 秒）")
    print("  请把摇杆【缓慢】从一端推到另一端并保持，再换另一轴，反复几次")

    try:
        while time.time() - t0 < args.seconds:
            data = dev.read(64, 200)
            if not data or data[0] != RID_INPUT:
                continue

            p = data[1:33]
            if len(p) < 30:
                continue

            seq = p[17] | (p[18] << 8) | (p[19] << 16) | (p[20] << 24)
            if seq == last_seq:
                continue
            last_seq = seq

            xr_raw = p[5] | (p[6] << 8)
            yr_raw = p[7] | (p[8] << 8)
            xpct = s8(p[3])
            ypct = s8(p[4])
            ctr_x = p[9] | (p[10] << 8)
            ctr_y = p[11] | (p[12] << 8)
            xmin = p[22] | (p[23] << 8)
            xmax = p[24] | (p[25] << 8)
            ymin = p[26] | (p[27] << 8)
            ymax = p[28] | (p[29] << 8)

            t_ms = int((time.time() - t0) * 1000)
            f.write(f"{t_ms},{seq},{xr_raw},{yr_raw},{xpct},{ypct},"
                    f"{p[2]},{p[1]},{ctr_x},{ctr_y},{xmin},{xmax},{ymin},{ymax},"
                    f"{p[30] if len(p) > 30 else -1}\n")
            n += 1

            xr[0] = min(xr[0], xr_raw); xr[1] = max(xr[1], xr_raw)
            yr[0] = min(yr[0], yr_raw); yr[1] = max(yr[1], yr_raw)
            xp[0] = min(xp[0], xpct);   xp[1] = max(xp[1], xpct)
            yp[0] = min(yp[0], ypct);   yp[1] = max(yp[1], ypct)

            if n % 40 == 0:
                f.flush()          # 便于随时查看部分数据
                raw = p[30] if len(p) > 30 else 0xFF
                print(f"  [{n:5d}] X={xr_raw:4d}({xpct:+4d}%) Y={yr_raw:4d}({ypct:+4d}%)  "
                      f"按键稳定=0x{p[1]:02X} 原始=0x{raw:02X}  dirs=0x{p[2]:02X}")
    finally:
        f.close()
        dev.close()

    print()
    print("=== 统计 ===")
    print(f"  样本数      : {n}")
    print(f"  X raw 范围  : [{xr[0]}, {xr[1]}]  跨度 {xr[1]-xr[0]}")
    print(f"  Y raw 范围  : [{yr[0]}, {yr[1]}]  跨度 {yr[1]-yr[0]}")
    print(f"  X pct 范围  : [{xp[0]}, {xp[1]}]")
    print(f"  Y pct 范围  : [{yp[0]}, {yp[1]}]")
    print(f"  数据已存到  : {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
