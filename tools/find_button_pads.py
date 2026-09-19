"""
Locate the gold button contact pads on the Xbox One button board, then crop a
zoomed patch around each one so the SWn silkscreen can be read.

Sampled colours (median on a pad vs on the board):
    pad gold  ~ (189,165,108)
    board     ~ ( 61,124, 98)
so gold = red clearly above blue, red above green, and reasonably bright.
"""
import json
import os
import numpy as np
from PIL import Image
from collections import deque

SRC = 'hardware_ref/xbox_pcb/3_按键板.jpg'
OUT = 'hardware_ref/xbox_pcb/pads'
os.makedirs(OUT, exist_ok=True)

im = Image.open(SRC).convert('RGB')
W0, H0 = im.size
a = np.asarray(im).astype(np.int16)
H, W = a.shape[:2]
print(f'{SRC}  {W0}x{H0}')

r, g, b = a[:, :, 0], a[:, :, 1], a[:, :, 2]

gold = (r > 150) & (g > 130) & (b < 165) & (r - b > 50) & (r - g > 8) & (g - b > 20)
print('gold px:', int(gold.sum()))

# clean up speckle with a 3x3 majority
m = gold
for _ in range(2):
    acc = np.zeros_like(m, dtype=np.int16)
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            acc += np.roll(np.roll(m, dy, axis=0), dx, axis=1)
    m = acc >= 6


def label(mask, min_px):
    seen = np.zeros_like(mask, dtype=bool)
    out = []
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
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    ny, nx = y + dy, x + dx
                    if 0 <= ny < H and 0 <= nx < W and mask[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True
                        q.append((ny, nx))
        if len(pts) < min_px:
            continue
        py = [p[0] for p in pts]
        pxx = [p[1] for p in pts]
        out.append({
            'cx': int(np.mean(pxx)), 'cy': int(np.mean(py)), 'n': int(len(pts)),
            'w': int(max(pxx) - min(pxx)), 'h': int(max(py) - min(py)),
        })
    return out


comps = label(m, 400)
# petal pads are round-ish blobs; drop long thin traces and huge shield areas
pads = [c for c in comps if 55 <= c['w'] <= 150 and 55 <= c['h'] <= 150
        and c['n'] > 900 and 0.5 < c['w'] / max(1, c['h']) < 2.0]
pads.sort(key=lambda p: (p['cy'] // 60, p['cx']))

print(f'\n=== {len(pads)} button pads ===')
for i, p in enumerate(pads):
    print(f"  #{i:<2} ({p['cx']:4d},{p['cy']:4d})  {p['w']}x{p['h']}  px={p['n']}")

# crop a patch around each pad, big enough to include the SWn silkscreen
PAD = 150
for i, p in enumerate(pads):
    x0 = max(0, p['cx'] - PAD)
    y0 = max(0, p['cy'] - PAD)
    x1 = min(W0, p['cx'] + PAD)
    y1 = min(H0, p['cy'] + PAD)
    crop = im.crop((x0, y0, x1, y1))
    crop = crop.resize((crop.width * 2, crop.height * 2), Image.LANCZOS)
    crop.save(f'{OUT}/pad_{i:02d}.png')

json.dump(pads, open('hardware_ref/xbox_pcb/pads_raw.json', 'w'), indent=1)
print(f'\ncrops written to {OUT}/  ({len(pads)} files)')
print('raw list -> hardware_ref/xbox_pcb/pads_raw.json')
