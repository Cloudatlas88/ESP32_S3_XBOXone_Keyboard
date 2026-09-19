"""把某个槽位临时禁用/启用（接线没接好时先止损）。

为什么要有这个：某个引脚被短到地时，对应槽位会一直"按下"，
于是它映射的那个键**一直被按住** —— 电脑上根本没法打字。
在查接线期间先把那个槽位禁掉，比拔线快。
"""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import cfg_proto as P

if len(sys.argv) < 3:
    print(__doc__)
    print('用法: python tools\\slot_enable.py <槽位号 0-23> <on|off>')
    sys.exit(2)

slot = int(sys.argv[1])
on = sys.argv[2].lower() in ('on', '1', 'true', '启用')

dev = P.Dev()
c = P.unpack_cfg(dev.read())

FLAG = 0x01  # KB_BTNFLAG_ENABLED
if on:
    c['buttons'][slot]['flags'] |= FLAG
else:
    c['buttons'][slot]['flags'] &= ~FLAG

dev.write(P.pack_cfg(c))
dev.save()

back = P.unpack_cfg(dev.read())
state = '启用' if (back['buttons'][slot]['flags'] & FLAG) else '禁用'
print(f"槽位 {slot}（{P.SLOT_NAMES[slot]}）已{state}，键码 0x{back['buttons'][slot]['keycode']:02X}")
