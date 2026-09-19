"""
Re-render the stick pad map so it is actually usable while soldering:
  - numbers placed outside the circles (no overlap)
  - pads grouped by my structural hypothesis, with a legend
  - a big zoom on the module so the pads are easy to see
"""
import json
import numpy as np
from PIL import Image, ImageDraw, ImageFont

SRC = 'hardware_ref/xbox_pcb/5_摇杆焊点.jpg'
RAW = json.load(open('hardware_ref/xbox_pcb/stick_pads.json'))
pads = RAW['pads']
X0, Y0, X1, Y1 = RAW['crop']

im = Image.open(SRC).convert('RGB')
crop = im.crop((X0, Y0, X1, Y1))

SC = 2
big = crop.resize((crop.width * SC, crop.height * SC), Image.LANCZOS)

# ── structural hypothesis, from the physical arrangement ──
# 4 corner pads = the module shell / mounting tabs (almost certainly shield/GND)
SHELL = {0, 1, 9, 10}
# bottom centre pair = the L3 push switch (the user says they already found these)
SWITCH = {11, 12}
# left column 3 pads = one potentiometer (3 pins)
POT_A = {2, 5, 6}
# right column pads = the other potentiometer
POT_B = {3, 7}
# everything else = on the main board, not the module
OTHER = set(range(len(pads))) - SHELL - SWITCH - POT_A - POT_B

GROUP_COLOR = {
    'shell':  (255, 140, 0),     # orange
    'switch': (160, 60, 255),    # purple
    'potA':   (0, 200, 255),     # cyan
    'potB':   (0, 230, 120),     # green
    'other':  (150, 150, 150),   # grey
}
GROUP_NAME = {
    'shell':  '外壳/屏蔽（4 角大焊盘）',
    'switch': 'L3 按下开关（你已找到）',
    'potA':   '电位器 A（左列 3 脚）',
    'potB':   '电位器 B（右列）',
    'other':  '在主板上的焊盘（不属于摇杆模块）',
}


def group_of(i):
    if i in SHELL: return 'shell'
    if i in SWITCH: return 'switch'
    if i in POT_A: return 'potA'
    if i in POT_B: return 'potB'
    return 'other'


d = ImageDraw.Draw(big)
try:
    fb = ImageFont.truetype('C:/Windows/Fonts/consolab.ttf', 40)
    fs = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 26)
except Exception:
    fb = fs = ImageFont.load_default()

for p in pads:
    i = p['id']
    x, y = p['cx'] * SC, p['cy'] * SC
    rr = max(p['w'], p['h']) * SC / 2 + 8
    col = GROUP_COLOR[group_of(i)]
    d.ellipse([x - rr, y - rr, x + rr, y + rr], outline=col, width=5)

    # put the number just outside the circle, on the side facing away from the
    # module centre so neighbouring labels do not collide
    cx0, cy0 = 400 * SC, 380 * SC
    vx, vy = x - cx0, y - cy0
    L = max(1.0, (vx * vx + vy * vy) ** 0.5)
    lx = x + vx / L * (rr + 30) - 20
    ly = y + vy / L * (rr + 30) - 22
    d.text((lx + 3, ly + 3), str(i), fill=(0, 0, 0), font=fb)
    d.text((lx, ly), str(i), fill=col, font=fb)

# legend
lx, ly = 24, 24
d.rectangle([lx - 12, ly - 12, lx + 900, ly + 34 * 6 + 16], fill=(255, 255, 255))
d.text((lx, ly), '摇杆模块焊盘（数字 = 编号，按颜色分组）', fill=(0, 0, 0), font=fs)
ly += 40
for g in ('potA', 'potB', 'switch', 'shell', 'other'):
    ids = sorted(i for i in range(len(pads)) if group_of(i) == g)
    if not ids: continue
    d.rectangle([lx, ly + 6, lx + 22, ly + 22], fill=GROUP_COLOR[g])
    d.text((lx + 32, ly), f'{GROUP_NAME[g]}   编号 {ids}', fill=(0, 0, 0), font=fs)
    ly += 34

big.save('hardware_ref/xbox_pcb/stick_pads_map.png')
print('-> hardware_ref/xbox_pcb/stick_pads_map.png', big.size)
for g in ('potA', 'potB', 'switch', 'shell', 'other'):
    ids = sorted(i for i in range(len(pads)) if group_of(i) == g)
    print(f'  {g:7s} {ids}')
