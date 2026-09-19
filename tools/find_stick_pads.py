"""
Locate the solder pads around the Xbox One thumbstick module.

The pads are bright tin/silver blobs on green PCB, so isolate them by
brightness + low saturation, then label connected components.

Output: numbered annotated image + JSON, so the user can say
"pad #3 to pad #7 reads 10k" and we are talking about the same pads.
"""
import json
import numpy as np
from PIL import Image, ImageDraw, ImageFont
from collections import deque

SRC = 'hardware_ref/xbox_pcb/5_摇杆焊点.jpg'
OUT_IMG = 'hardware_ref/xbox_pcb/stick_pads_numbered.png'
OUT_JSON = 'hardware_ref/xbox_pcb/stick_pads.json'

im = Image.open(SRC).convert('RGB')
W0, H0 = im.size

# work on the cropped region that contains the stick module
sx, sy = W0 / 692, H0 / 923
X0, Y0, X1, Y1 = int(170 * sx), int(400 * sy), int(490 * sy), int(740 * sy)
crop = im.crop((X0, Y0, X1, Y1))
a = np.asarray(crop).astype(np.int16)
H, W = a.shape[:2]
print(f'crop {crop.size}')

r, g, b = a[:, :, 0], a[:, :, 1], a[:, :, 2]
mx = np.maximum(np.maximum(r, g), b)
mn = np.minimum(np.minimum(r, g), b)

# bright + low saturation  ->  tin solder
bright = (mx > 130) & ((mx - mn) < 60)
print('bright px:', int(bright.sum()))

m = bright
for _ in range(2):
    acc = np.zeros_like(m, dtype=np.int16)
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            acc += np.roll(np.roll(m, dy, 0), dx, 1)
    m = acc >= 6


def label(mask, min_px):
    seen = np.zeros_like(mask, dtype=bool)
    out = []
    ys, xs = np.nonzero(mask)
    for sy_, sx_ in zip(ys, xs):
        if seen[sy_, sx_]:
            continue
        q = deque([(sy_, sx_)])
        seen[sy_, sx_] = True
        pts = []
        while q:
            y, x = q.popleft()
            pts.append((y, x))
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    ny, nx = y + dy, x + dx
                    if 0 <= ny < H and 0 <= nx < W and mask[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True
                        q.append((ny, nx))
        if len(pts) < min_px:
            continue
        py = [p[0] for p in pts]
        px = [p[1] for p in pts]
        w, h = max(px) - min(px), max(py) - min(py)
        fill = len(pts) / max(1, (w + 1) * (h + 1))
        out.append({'cx': int(np.mean(px)), 'cy': int(np.mean(py)),
                    'n': int(len(pts)), 'w': int(w), 'h': int(h),
                    'fill': round(float(fill), 2)})
    return out


comps = label(m, 150)
# solder pads: chunky and reasonably solid.  the long white silkscreen lines
# and the "TP6x" text are thin -> low fill or extreme aspect ratio.
pads = [c for c in comps
        if 25 <= c['w'] <= 140 and 25 <= c['h'] <= 140
        and c['fill'] >= 0.45
        and 0.45 < c['w'] / max(1, c['h']) < 2.3]
pads.sort(key=lambda p: (p['cy'] // 60, p['cx']))

print(f'\n=== {len(pads)} pads ===')
for i, p in enumerate(pads):
    print(f"  #{i:<2} ({p['cx']:4d},{p['cy']:4d})  {p['w']}x{p['h']}  fill={p['fill']}  px={p['n']}")

# ---- annotated image (scaled up for readability) ----
SC = 2
big = crop.resize((crop.width * SC, crop.height * SC), Image.LANCZOS)
d = ImageDraw.Draw(big)
try:
    font = ImageFont.truetype('C:/Windows/Fonts/consolab.ttf', 34)
except Exception:
    font = ImageFont.load_default()

for i, p in enumerate(pads):
    x, y = p['cx'] * SC, p['cy'] * SC
    rr = max(p['w'], p['h']) * SC / 2 + 10
    d.ellipse([x - rr, y - rr, x + rr, y + rr], outline=(255, 0, 0), width=4)
    d.ellipse([x - 4, y - 4, x + 4, y + 4], fill=(255, 255, 0))
    d.text((x - 22, y - rr - 40), f'{i}', fill=(255, 40, 40), font=font)

big.save(OUT_IMG)

out = [{'id': i, 'cx': p['cx'], 'cy': p['cy'], 'w': p['w'], 'h': p['h']}
       for i, p in enumerate(pads)]
json.dump({'crop': [X0, Y0, X1, Y1], 'pads': out},
          open(OUT_JSON, 'w'), indent=1)
print(f'\n-> {OUT_IMG}')
print(f'-> {OUT_JSON}')
