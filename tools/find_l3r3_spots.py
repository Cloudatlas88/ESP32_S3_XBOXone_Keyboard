"""
Pick concrete spots for the L3 / R3 markers.

Rules:
  - must be blank (I cannot see the image, so I measure)
  - must be close to its stick dot (so the association is obvious)
  - must not sit near any existing dot or label box
"""
import json
import numpy as np
from PIL import Image

SRC = 'host/KbConfigurator/Assets/xbox-controller.png'
im = Image.open(SRC).convert('RGB')
a = np.asarray(im).astype(np.int16)
H, W = a.shape[:2]
ink = (np.abs(a - np.array([240, 241, 243])).sum(axis=2) > 45)

raw = json.load(open('tools/xbox_diagram_raw.json', encoding='utf-8'))
dots = [tuple(d['px']) for d in raw['dots']]
labels = [tuple(l['px']) for l in raw['labels']]


def blank(cx, cy, half=24):
    x0, x1 = max(0, cx - half), min(W, cx + half + 1)
    y0, y1 = max(0, cy - half), min(H, cy + half + 1)
    return 1.0 - ink[y0:y1, x0:x1].mean()


def clash(cx, cy, pad=46):
    """too close to an existing dot, or inside a label box? -> say what"""
    for dx, dy in dots:
        if abs(cx - dx) < pad and abs(cy - dy) < pad:
            return f'dot({dx},{dy})'
    for x0, y0, x1, y1 in labels:
        if x0 - 16 < cx < x1 + 16 and y0 - 16 < cy < y1 + 16:
            return f'label({x0},{y0},{x1},{y1})'
    return None


CANDIDATES = {}   # 上面手挑的点都不够干净，改成环形搜索


def ring_search(cx, cy, r0=70, r1=150, step=8):
    """all clean spots on rings around a stick dot"""
    import math
    out = []
    for r in range(r0, r1 + 1, step):
        for deg in range(0, 360, 6):
            t = math.radians(deg)
            x = int(round(cx + r * math.cos(t)))
            y = int(round(cy + r * math.sin(t)))
            if not (0 <= x < W and 0 <= y < H):
                continue
            if blank(x, y) < 0.995:
                continue
            if clash(x, y) is not None:
                continue
            out.append((x, y, r, deg))
    return out


LEFT_STICK = (890, 640)
RIGHT_STICK = (1435, 641)
CX = (LEFT_STICK[0] + RIGHT_STICK[0]) / 2       # mirror axis of the diagram

for name, (cx, cy) in (('L3', LEFT_STICK), ('R3', RIGHT_STICK)):
    spots = ring_search(cx, cy)
    print(f'=== {name}: {len(spots)} clean spots around ({cx},{cy}) ===')
    for x, y, r, deg in spots[:12]:
        print(f'   ({x:4d},{y:4d})  r={r:3d} deg={deg:3d}  mirror-x={int(2*CX-x)}')
    print()

# ---- pick the CLOSEST clean spot to each stick (the badge should sit next to its stick) ----
print('=== closest clean spot per stick ===')
chosen = {}
for name, (cx, cy) in (('L3', LEFT_STICK), ('R3', RIGHT_STICK)):
    spots = ring_search(cx, cy)
    spots.sort(key=lambda s: s[2])              # by radius
    if not spots:
        print(f'{name}: no clean spot found'); continue
    x, y, r, deg = spots[0]
    chosen[name] = (x, y)
    print(f'  {name}: ({x},{y})  r={r}  offset=({x-cx:+d},{y-cy:+d})  '
          f'norm=({x/W:.4f},{y/H:.4f})')
    print(f'       (另有 {len(spots)} 个备选，最近 5 个: ' +
          ', '.join(f'({a},{b})r={rr}' for a, b, rr, _ in spots[:5]) + ')')

if len(chosen) == 2:
    print(f'\n>>> L3=({chosen["L3"][0]},{chosen["L3"][1]})  '
          f'R3=({chosen["R3"][0]},{chosen["R3"][1]})')


