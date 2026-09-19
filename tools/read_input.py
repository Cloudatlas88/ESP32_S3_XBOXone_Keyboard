"""读一帧 Input Report，把 26 个槽位、原始 GPIO 电平、四轴状态都打出来。

用途：区分"某个键一直被按住"到底是**固件配错引脚**还是**接线把引脚拉低了**。
槽位位图告诉我们哪个逻辑槽位是按下，btn_raw 告诉我们哪根引脚真的读到了低电平。

★ 帧偏移必须跟着固件 app_config.h 的 IN_OFF_* 走。
  这里曾经因为没跟着改而**解码错位** —— 把 axis_fault 当成了原始电平的低字节，
  报告出一个根本不存在的"引脚被拉低"。**诊断工具自己错了比没有还糟**，
  所以偏移全部提成常量，改的时候一眼看得到有几处。
"""
import sys, time

import hid

VID, PID = 0x3554, 0xFA09
RID_INPUT = 0x04

SLOT_NAMES = ["A", "B", "X", "Y", "LB", "RB", "View", "Menu", "Xbox", "Share",
              "L3", "R3", "十字键↑", "十字键↓", "十字键←", "十字键→",
              "左摇杆↑", "左摇杆↓", "左摇杆←", "左摇杆→",
              "右摇杆↑", "右摇杆↓", "右摇杆←", "右摇杆→",
              "LT", "RT"]

# 槽位 → 引脚（0x0005；24/25 = LT/RT 接在 GPIO40/41）
SLOT_GPIO = {0: 6, 1: 7, 2: 8, 3: 9, 4: 10, 5: 11, 6: 12, 7: 13, 8: 14, 9: 15,
             10: 16, 11: 39, 12: 17, 13: 18, 14: 21, 15: 38,
             24: 40, 25: 41}

# ── 帧布局（0x0005 / 54 字节）──
OFF_SLOTS = 1        # 4 字节
OFF_DIRS = 5         # 3 字节：十字键 / 左摇杆 / 右摇杆
OFF_STAT = 8
OFF_RAW = 9          # 4 轴 × uint16
OFF_CENTER = 17
OFF_AXIS_FAULT = 33
OFF_BTN_RAW = 34     # 4 字节

AXIS_NAMES = ["左摇杆X", "左摇杆Y", "右摇杆X", "右摇杆Y"]


def find_vendor():
    for d in hid.enumerate(VID, PID):
        if d.get('usage_page') == 0xFF00:
            return d
    return None


def main():
    cand = find_vendor()
    if cand is None:
        print('找不到厂商配置集合 UP:0xFF00（设备没连？还是固件没起来？）')
        return 1

    dev = hid.device()
    dev.open_path(cand['path'])
    print(f"已打开：{cand.get('product_string')}  if={cand.get('interface_number')}\n")

    deadline = time.time() + 6
    last = None
    while time.time() < deadline:
        d = dev.read(64, timeout_ms=300)
        if d and d[0] == RID_INPUT:
            last = d[1:]
    dev.close()

    if last is None:
        print('没收到 Input Report')
        return 1

    def u32(off):
        return (last[off] | (last[off + 1] << 8)
                | (last[off + 2] << 16) | (last[off + 3] << 24))

    def u16(off):
        return last[off] | (last[off + 1] << 8)

    slots = u32(OFF_SLOTS)
    raw = u32(OFF_BTN_RAW)
    fault = last[OFF_AXIS_FAULT]

    print(f"槽位位图 : 0x{slots:08X}   （已防抖）")
    print(f"原始电平 : 0x{raw:08X}   （bit=1 表示该脚读到【低电平】= 按下）")
    print(f"方向     : 十字键=0x{last[OFF_DIRS]:02X}  "
          f"左摇杆=0x{last[OFF_DIRS+1]:02X}  右摇杆=0x{last[OFF_DIRS+2]:02X}")
    bad = [AXIS_NAMES[i] for i in range(4) if fault & (1 << i)]
    print(f"轴故障   : 0x{fault:02X}" +
          (f"  -> {', '.join(bad)} 已禁用" if bad else "  (四轴都正常)"))
    print()
    print("四轴读数（原始 / 中心 / 行程）：")
    for i, nm in enumerate(AXIS_NAMES):
        r = u16(OFF_RAW + i * 2)
        c = u16(OFF_CENTER + i * 2)
        print(f"  {nm:<8} raw={r:5}  center={c:5}  "
              f"{'【已禁用】' if fault & (1 << i) else ''}")
    print()

    print(f"按下的槽位（{len(SLOT_NAMES)} 个里）：")
    any_on = False
    for i in range(len(SLOT_NAMES)):
        if slots & (1 << i):
            any_on = True
            g = SLOT_GPIO.get(i)
            print(f"  槽位 {i:2d}  {SLOT_NAMES[i]:<8}  引脚 "
                  f"{'GPIO' + str(g) if g else '（ADC 派生）'}")
    if not any_on:
        print("  （无）")

    print("\n原始电平里为低的引脚：")
    any_raw = False
    for i in range(len(SLOT_NAMES)):
        if raw & (1 << i):
            any_raw = True
            g = SLOT_GPIO.get(i)
            print(f"  槽位 {i:2d}  {SLOT_NAMES[i]:<8}  引脚 "
                  f"{'GPIO' + str(g) if g else '（ADC 派生，不该出现在这里）'}")
    if not any_raw:
        print("  （无）")

    print()
    if raw == 0:
        print("结论：没有任何引脚被拉低 —— 所有数字输入都正常读高")
    elif slots == raw:
        print("结论：槽位位图与原始电平【一致】—— 引脚确实被拉低了，"
              "属接线问题（焊到了公共端 / 短路 / 开关一直闭合）")
    else:
        print(f"结论：槽位位图 0x{slots:X} 与原始电平 0x{raw:X} 【不一致】"
              f" —— 差值通常是防抖还在收敛（原始已变、稳定值还没跟上），"
              f"或者该槽位被禁用/绑了宏")
    return 0


if __name__ == '__main__':
    sys.exit(main())
