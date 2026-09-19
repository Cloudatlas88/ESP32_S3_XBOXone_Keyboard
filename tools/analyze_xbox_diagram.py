"""
Analyze the Xbox controller diagram to locate:
  1. leader-line endpoint dots  -> the real button positions on the controller
  2. text label bounding boxes  -> where the editable mapping controls go

Method:
  - blue (0,174,239) mask = leader lines + endpoint dots
  - erode the blue mask: a ~4px line disappears, a ~10px filled dot survives
  - what survives = dot centres
  - black mask, drop the huge outline component, group small glyphs into text lines
"""
import numpy as np
from PIL import Image
from collections import deque

SRC = 'host/KbConfigurator/Assets/xbox-controller.png'

im = Image.open(SRC).convert('RGB')
a = np.asarray(im).astype(np.int16)
H, W = a.shape[:2]
print(f'image {W}x{H}')

r, g, b = a[:, :, 0], a[:, :, 1], a[:, :, 2]

blue = (b > 150) & (b - r > 60) & (b - g > 20)
black = (r < 90) & (g < 90) & (b < 90)
print(f'blue px {blue.sum()}   black px {black.sum()}')


def erode(mask, k):
    """binary erosion with a kxk square (all-ones neighbourhood)"""
    out = mask.copy()
    for dy in range(-(k // 2), k // 2 + 1):
        for dx in range(-(k // 2), k // 2 + 1):
            out &= np.roll(np.roll(mask, dy, axis=0), dx, axis=1)
    return out


def label(mask, min_px=4):
    """connected components (4-neighbour), returns [(cx,cy,size,bbox)]"""
    seen = np.zeros_like(mask, dtype=bool)
    res = []
    ys, xs = np.nonzero(mask)
    for sy, sx in zip(ys, xs):
        if seen[sy, sx]:
            continue
        q = deque([(sy, sx)])
        seen[sy, sx] = True
        pts = []
        while q:
            y, x = q.popleft()
            pts.append((y, x))
            for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                ny, nx = y + dy, x + dx
                if 0 <= ny < H and 0 <= nx < W and mask[ny, nx] and not seen[ny, nx]:
                    seen[ny, nx] = True
                    q.append((ny, nx))
        if len(pts) < min_px:
            continue
        py = [p[0] for p in pts]
        px_ = [p[1] for p in pts]
        res.append((int(np.mean(px_)), int(np.mean(py)), len(pts),
                    (min(px_), min(py), max(px_), max(py))))
    return res


# ---- 1. dots = erosion survivors of the blue mask ----
survivors = erode(blue, 7)
dots = label(survivors, min_px=6)
dots.sort(key=lambda d: (d[1], d[0]))
print(f'\n=== {len(dots)} blue dots (leader-line endpoints) ===')
for cx, cy, n, bb in dots:
    print(f'  ({cx:4d},{cy:4d})  px={n:4d}  bbox={bb}')

# ---- 2. text labels = glyphs grouped into horizontal lines ----
glyphs = [c for c in label(black, min_px=8) if c[2] < 4000]   # drop the big outline
print(f'\n=== {len(glyphs)} small black components (glyphs) ===')

glyphs.sort(key=lambda c: (c[1], c[0]))
lines = []
for cx, cy, n, bb in glyphs:
    placed = False
    for ln in lines:
        if abs(ln['y'] - cy) <= 22:
            ln['items'].append((cx, cy, bb))
            ln['y'] = int(np.mean([i[1] for i in ln['items']]))
            placed = True
            break
    if not placed:
        lines.append({'y': cy, 'items': [(cx, cy, bb)]})

merged = []
for ln in lines:
    # ★ 同一行上左右两侧各有一个标签（比如 "Left trigger (LT)" 和 "Right trigger (RT)"），
    #   必须按 x 方向的空隙把它们拆成独立标签，否则会合成一个横跨整张图的巨框。
    items = sorted(ln['items'], key=lambda i: i[0])
    seg = [items[0]]
    for it in items[1:]:
        if it[0] - seg[-1][0] > 70:          # 空隙够大 → 换一个标签
            merged.append(seg)
            seg = []
        seg.append(it)
    merged.append(seg)

boxes = []
for seg in merged:
    xs0 = min(i[2][0] for i in seg)
    xs1 = max(i[2][2] for i in seg)
    ys0 = min(i[2][1] for i in seg)
    ys1 = max(i[2][3] for i in seg)
    boxes.append((xs0, ys0, xs1, ys1, len(seg)))

boxes.sort(key=lambda m: (m[1], m[0]))
print(f'\n=== {len(boxes)} label boxes (x0,y0,x1,y1,n_glyphs) ===')
for m in boxes:
    print(f'  {m}')

# ---- normalised output for the app ----
import json

def nx(v):
    return round(float(v) / W, 4)

def ny(v):
    return round(float(v) / H, 4)

out = {
    'source': 'xbox-controller.png',
    'imageW': int(W),
    'imageH': int(H),
    # 连接线端点 = 手柄上按键的真实位置（实时高亮画在这里）
    'dots': [{'x': nx(d[0]), 'y': ny(d[1]), 'px': [int(d[0]), int(d[1])]} for d in dots],
    # 文字标签框 = 可编辑映射控件该放的位置
    'labels': [{'x0': nx(b[0]), 'y0': ny(b[1]), 'x1': nx(b[2]), 'y1': ny(b[3]),
                'px': [int(v) for v in b[:4]], 'n': int(b[4])} for b in boxes],
}
json.dump(out, open('tools/xbox_diagram_raw.json', 'w', encoding='utf-8'),
          indent=1, ensure_ascii=False)
print('\nwritten tools/xbox_diagram_raw.json')
print(f'normalised: dots={len(out["dots"])} labels={len(out["labels"])}')

