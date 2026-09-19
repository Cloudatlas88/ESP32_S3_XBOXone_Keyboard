"""
Sensitivity sweep: are we missing pads? Re-run the tin-pad detector with much
looser thresholds and diff against the 13 already found.
"""
import json
import numpy as np
from PIL import Image

SRC = 'hardware_ref/xbox_pcb/5_摇杆焊点.jpg'
RAW = json.load(open('hardware_ref/xbox_pcb/stick_pads.json'))
X0, Y0, X1, Y1 = RAW['crop']
known = [(p['cx'], p['cy']) for p in RAW['pads']]

im = Image.open(SRC).convert('RGB').crop((X0, Y0, X1, Y1))
a = np.asarray(im).astype(np.float32)
r, g, b = a[:, :, 0], a[:, :, 1], a[:, :, 2]
mx = a.max(2); mn = a.min(2)
val = mx
sat = np.where(mx > 0, (mx - mn) / np.maximum(mx, 1), 0)

# looser than before: brightness 130 (was ~150) and sat < 0.40 (was ~0.30)
mask = (val > 130) & (sat < 0.40)

try:
    from scipy import ndimage
except ImportError:
    raise SystemExit('need scipy')
lab, n = ndimage.label(mask)
print(f'mask px = {mask.sum()}, components = {n}')

found = []
for sl in ndimage.find_objects(lab):
    h = sl[0].stop - sl[0].start
    w = sl[1].stop - sl[1].start
    area = (lab[sl] > 0).sum()
    if not (8 <= h <= 140 and 8 <= w <= 140):
        continue
    if area < 90:
        continue
    cy = (sl[0].start + sl[0].stop) / 2
    cx = (sl[1].start + sl[1].stop) / 2
    fill = area / (h * w)
    if fill < 0.30:
        continue
    found.append((cx, cy, w, h, fill, area))

print(f'candidate blobs = {len(found)}')
new = []
for cx, cy, w, h, fill, area in found:
    d = min(((cx - kx) ** 2 + (cy - ky) ** 2) ** 0.5 for kx, ky in known)
    if d > 28:
        new.append((cx, cy, w, h, fill, area, d))

print(f'\nblobs NOT matching a known pad (>{28}px away): {len(new)}')
for cx, cy, w, h, fill, area, d in sorted(new, key=lambda t: -t[5])[:25]:
    print(f'  ({cx:6.0f},{cy:6.0f}) {w:3.0f}x{h:3.0f} area={area:5d} fill={fill:.2f} nearest_known={d:.0f}px')

# also: report every known pad's own re-detection so we can see which are weak
print('\nknown pads re-detected at loose threshold:')
for i, (kx, ky) in enumerate(known):
    best = None
    for cx, cy, w, h, fill, area in found:
        d = ((cx - kx) ** 2 + (cy - ky) ** 2) ** 0.5
        if best is None or d < best[0]:
            best = (d, cx, cy, fill, area)
    flag = 'OK ' if best[0] < 28 else 'MISS'
    print(f'  #{i:2d} ({kx:5.0f},{ky:5.0f}) -> {flag} d={best[0]:5.0f} fill={best[3]:.2f} area={best[4]:5d}')
