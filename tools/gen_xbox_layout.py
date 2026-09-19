"""
Generate the authoritative hotspot layout for the Xbox diagram.

Inputs:
  tools/xbox_diagram_raw.json    raw dots + text-label boxes (pixel analysis)
  tools/xbox_dot_labels.json     dot -> label association (leader-line tracing)

The endpoint->button names were originally guessed from coordinates while
the image was unviewable, and 5 of 15 turned out wrong
(see the commit message). This script uses the traced association instead.

Outputs:
  host/KbConfigurator/Assets/layout.json   consumed by the UI at runtime
  tools/xbox_layout_preview.png            overlay for a visual sanity check
"""
import json
import numpy as np
from PIL import Image, ImageDraw, ImageFont

SRC = 'host/KbConfigurator/Assets/xbox-controller.png'
RAW = json.load(open('tools/xbox_diagram_raw.json'))
TRACE = json.load(open('tools/xbox_dot_labels.json'))

im = Image.open(SRC).convert('RGB')
W, H = im.size
arr = np.asarray(im).astype(np.int16)
mx = arr.max(2)
mn = arr.min(2)
# the drawing's background is a flat light grey; the controller outline, the
# labels and the leaders are all noticeably darker
background = (mn > 200) & ((mx - mn) < 25)
print(f'background px = {int(background.sum())} / {W*H}')

# Leader lines are the same pure cyan-blue the tracer uses. The first version of
# blank_spot only avoided background and the text boxes, so the R3 badge landed
# right on top of the D-pad's leader line and the L3 badge crowded the LB one.
r_ch, g_ch, b_ch = arr[:, :, 0], arr[:, :, 1], arr[:, :, 2]
blue = (b_ch > 150) & (b_ch - r_ch > 60) & (g_ch > 120) & (g_ch < 220) & (r_ch < 130)
print(f'leader-line px = {int(blue.sum())}')

# ── traced dot -> label name ──
by_name = {t['best_label']: t['dot'] for t in TRACE}
print('\ntraced associations:')
for t in TRACE:
    print(f"  dot {t['dot']:2d} px{tuple(RAW['dots'][t['dot']]['px'])} -> {t['best_label']}")

label_box = {}
for lb in RAW['labels']:
    if lb['n'] >= 6:
        label_box.setdefault(lb['px'][1] // 10, lb)   # any box; refined below
# map label name -> box, using the same index order the tracer used
named = [lb for lb in RAW['labels'] if lb['n'] >= 6]
NAMES = ['Xbox button', 'Left bumper (LB)', 'Right bumper (RB)',
         'Left trigger (LT)', 'Right trigger (RT)', 'Y button',
         'Left stick', 'B button', 'X button', 'View button', 'A button',
         'Directional Pad', '(D-pad)', 'Menu button', 'Right Stick', 'Share button']
box_of = dict(zip(NAMES, [lb['px'] for lb in named]))


def dot_px(name):
    return RAW['dots'][by_name[name]]['px']


def norm(px):
    return [round(px[0] / W, 4), round(px[1] / H, 4)]


def box_of_names(*names):
    """Union of several label boxes (e.g. 'Directional Pad' + '(D-pad)')."""
    bs = [box_of[n] for n in names if n in box_of]
    return [min(b[0] for b in bs), min(b[1] for b in bs),
            max(b[2] for b in bs), max(b[3] for b in bs)]


# ── find blank spots for the L3 / R3 badges ──
# They must sit near their stick, on background only, and clear of every dot
# and label box. The earlier coordinates in the design doc assumed the RIGHT
# stick was at (1435,641), which the tracing proved is actually the X button,
# so those numbers were invalid and are recomputed here.
dot_pxs = [d['px'] for d in RAW['dots']]
all_label_boxes = [lb['px'] for lb in named]


def blank_spot(cx, cy, bw=130, bh=64):
    """Find the emptiest patch near a stick for an L3/R3 badge.

    A patch qualifies only if it is nearly all background AND contains almost no
    leader-line pixels AND clears every dot and text label. The blue test was
    missing at first, which put the badges on top of the leaders.
    """
    best = None
    for rad in range(90, 360, 10):
        for ang in range(0, 360, 5):
            a = np.deg2rad(ang)
            x = int(cx + rad * np.cos(a))
            y = int(cy + rad * np.sin(a) * 0.78)     # squash: diagram is wide
            x0, y0 = x - bw // 2, y - bh // 2
            x1, y1 = x0 + bw, y0 + bh
            if x0 < 8 or y0 < 8 or x1 >= W - 8 or y1 >= H - 8:
                continue

            patch = background[y0:y1, x0:x1]
            score = patch.mean()
            score -= 3.0 * blue[y0:y1, x0:x1].mean()      # 引线是大忌
            # keep clear of existing dots
            for dx, dy in dot_pxs:
                if x0 - 46 < dx < x1 + 46 and y0 - 46 < dy < y1 + 46:
                    score -= 1.0
            # keep clear of the text labels
            for lx0, ly0, lx1, ly1 in all_label_boxes:
                if not (x1 < lx0 or x0 > lx1 or y1 < ly0 or y0 > ly1):
                    score -= 1.0
            if best is None or score > best[0]:
                best = (score, x, y)
        if best and best[0] > 0.98:
            break
    return best


lstick = dot_px('Left stick')
rstick = dot_px('Right Stick')
l3 = blank_spot(*lstick)
r3 = blank_spot(*rstick)
print(f'\nL3 badge -> px({l3[1]},{l3[2]}) blank score {l3[0]:.3f}  (stick at {tuple(lstick)})')
print(f'R3 badge -> px({r3[1]},{r3[2]}) blank score {r3[0]:.3f}  (stick at {tuple(rstick)})')

# ── assert the badge spots are actually usable ──
# Without this the "badge sits on a leader line" defect would silently come back
# the next time the image or the search parameters change.
BADGE_W, BADGE_H = 130, 64
MIN_DOT_GAP = 70          # badge centre to any hotspot centre
for name, spot, owner in (('L3', l3, lstick), ('R3', r3, rstick)):
    x, y = spot[1], spot[2]
    x0, y0 = x - BADGE_W // 2, y - BADGE_H // 2
    x1, y1 = x0 + BADGE_W, y0 + BADGE_H

    bg = background[y0:y1, x0:x1].mean()
    bl = blue[y0:y1, x0:x1].mean()
    assert bg > 0.95, f'{name} 徽章处背景占比只有 {bg:.3f}，会压到图上内容'
    assert bl < 0.005, f'{name} 徽章处有 {bl:.4f} 的引线像素，会盖住引线'

    nearest = min(((x - dx) ** 2 + (y - dy) ** 2) ** 0.5 for dx, dy in dot_pxs)
    assert nearest > MIN_DOT_GAP, f'{name} 徽章离最近热区只有 {nearest:.0f}px'
    print(f'  {name}: 背景 {bg:.3f} / 引线 {bl:.4f} / 离最近热区 {nearest:.0f}px  —— 通过')

# ── slot numbering (26 slots) ──
# ★ LT/RT 追加在**末尾**（24/25）—— 不动已有 0..23 的编号，
#   这样配置数组、SlotMap、宏绑定都只是"多两项"，不用整体平移。
#   改编号是这类改动里最容易出错、也最难查的地方。
SLOTS = ['A', 'B', 'X', 'Y', 'LB', 'RB', 'View', 'Menu', 'Xbox', 'Share',
         'L3', 'R3',
         'D↑', 'D↓', 'D←', 'D→',
         'L↑', 'L↓', 'L←', 'L→',
         'R↑', 'R↓', 'R←', 'R→',
         'LT', 'RT']
slot_index = {n: i for i, n in enumerate(SLOTS)}


def single(id_, cn, dname, slot, labelnames=None):
    p = dot_px(dname)
    return dict(id=id_, kind='single', name=cn, diagramLabel=dname,
                slot=slot_index[slot], slots=[slot_index[slot]],
                dot=norm(p), dotPx=p,
                labelPx=box_of_names(*(labelnames or [dname])))


def dir4(id_, cn, dname, prefix, labelnames=None):
    p = dot_px(dname)
    return dict(id=id_, kind='dir4', name=cn, diagramLabel=dname,
                slots=[slot_index[prefix + s] for s in '↑↓←→'],
                dot=norm(p), dotPx=p,
                labelPx=box_of_names(*(labelnames or [dname])))


def badge(id_, cn, slot, spot, attach):
    """A hotspot with no leader-line endpoint on the diagram (L3 / R3).

    They still occupy a real config slot - leaving them out of `items` made the
    slot set 0..23 minus {10,11}, which the sanity assertion at the end caught.
    """
    px = [spot[1], spot[2]]
    return dict(id=id_, kind='badge', name=cn, diagramLabel='',
                slot=slot_index[slot], slots=[slot_index[slot]],
                dot=norm(px), dotPx=px, labelPx=[], attach=attach)


items = [
    single('a',     'A 键',    'A button',          'A'),
    single('b',     'B 键',    'B button',          'B'),
    single('x',     'X 键',    'X button',          'X'),
    single('y',     'Y 键',    'Y button',          'Y'),
    single('lb',    'LB 肩键', 'Left bumper (LB)',  'LB'),
    single('rb',    'RB 肩键', 'Right bumper (RB)', 'RB'),
    single('view',  'View 键', 'View button',       'View'),
    single('menu',  'Menu 键', 'Menu button',       'Menu'),
    single('xbox',  'Xbox 键', 'Xbox button',       'Xbox'),
    single('share', 'Share 键','Share button',      'Share'),
    badge('l3', 'L3', 'L3', l3, 'lstick'),
    badge('r3', 'R3', 'R3', r3, 'rstick'),
    dir4('dpad',   '十字键',   '(D-pad)',           'D',
         labelnames=['Directional Pad', '(D-pad)']),   # ★ 两行都要盖住
    dir4('lstick', '左摇杆',   'Left stick',        'L'),
    dir4('rstick', '右摇杆',   'Right Stick',       'R'),
]

# ★ LT / RT 现在是**真实槽位**（24 / 25）。
#   2026-09-15 用户实际把扳机接上了（GPIO40/41，数字量、阈值式判定），
#   于是从"暂缓项"提升成普通单键项 —— 图上原来那两个灰掉的圈现在可以编辑了。
items.append(single('lt', 'LT 扳机', 'Left trigger (LT)', 'LT'))
items.append(single('rt', 'RT 扳机', 'Right trigger (RT)', 'RT'))
deferred = []

layout = dict(
    version=1,
    image='Assets/xbox-controller.png',
    imageW=W, imageH=H,
    sourceNote='坐标由 tools/trace_xbox_leaders.py 追踪引线得到；'
               '端点名称经该脚本校正（设计文档早期版本有 5 个名称有误）',
    slots=SLOTS,
    items=items,
    deferred=deferred,
)

# Sanity: the items must cover every slot exactly once. Without this the L3/R3
# slots were silently absent (items covered only 22 of 24).
covered = sorted(s for it in items for s in it['slots'])
assert covered == list(range(len(SLOTS))), (
    f'槽位覆盖不完整: 得到 {len(covered)} 个, 期望 {len(SLOTS)}; '
    f'缺失 {sorted(set(range(len(SLOTS))) - set(covered))}')
print(f'\n槽位覆盖校验通过: {len(covered)} 个槽位 (0..{len(SLOTS)-1}) 各一次')

OUT_JSON = 'host/KbConfigurator/Assets/layout.json'
OUT_DEFAULT = 'host/KbConfigurator/Assets/layout.default.json'

for path in (OUT_JSON, OUT_DEFAULT):
    json.dump(layout, open(path, 'w', encoding='utf-8'),
              ensure_ascii=False, indent=1)

print(f'-> {OUT_JSON}  ({len(items)} 交互项 + {len(deferred)} 暂缓项)')
print(f'-> {OUT_DEFAULT}  ← 同一份内容，作为「恢复默认」的参照')
print('   ★ 为什么要存两份：「布局校准」模式会把拖动后的坐标写回 layout.json，')
print('     没有出厂备份的话「恢复默认」就没有可回去的地方。')

# ── preview ──
big = im.copy()
d = ImageDraw.Draw(big)
try:
    fs = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 30)
except Exception:
    fs = ImageFont.load_default()

for it in items + deferred:
    x, y = it['dotPx']
    if it['kind'] == 'badge':
        d.rounded_rectangle([x - 60, y - 30, x + 60, y + 30], radius=14,
                            outline=(0, 90, 220), width=6)
        d.text((x - 52, y - 18), it['id'].upper(), fill=(0, 80, 210), font=fs)
        continue
    r = 34
    col = (200, 200, 200) if it['kind'] == 'deferred' else (255, 0, 0)
    d.ellipse([x - r, y - r, x + r, y + r], outline=col, width=6)
    if it['labelPx']:
        lx0, ly0, lx1, ly1 = it['labelPx']
        d.rectangle([lx0, ly0, lx1, ly1], outline=(0, 150, 0), width=4)
    d.text((x + r + 6, y - 18), it['name'], fill=(180, 0, 0), font=fs)

big.save('tools/xbox_layout_preview.png')
print('-> tools/xbox_layout_preview.png')
