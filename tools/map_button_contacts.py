"""
Number every button contact on the Xbox One button board.

The contacts are interdigitated gold rings (comb fingers), not simple pads, so
detect gold blobs that are ring-shaped (a hole in the middle where the LED /
center part sits) and reasonably sized, then annotate the whole board.
Also mark the white silicone D-pad membrane, whose 4 contacts are hidden under it.
Coordinates are written to btnboard_contacts.json.
"""
import json
import numpy as np
from PIL import Image, ImageDraw, ImageFont
from collections import deque

SRC = 'hardware_ref/xbox_pcb/3_按键板.jpg'
im = Image.open(SRC).convert('RGB')
W0, H0 = im.size
a = np.asarray(im).astype(np.int16)
H, W = a.shape[:2]
r, g, b = a[:, :, 0], a[:, :, 1], a[:, :, 2]

# gold / cream plating: red clearly above blue, and fairly bright
gold = (r > 140) & (r - b > 35) & (r - g > 4) & (g - b > 12) & (r > g)
print('gold px:', int(gold.sum()))

# despeckle: ONE gentle pass only. Two passes eroded the thin comb fingers of
# some contacts (e.g. SW13) completely away, which silently lost them.
m_raw = gold.copy()
acc = np.zeros_like(m_raw, dtype=np.int16)
for dy in (-1, 0, 1):
    for dx in (-1, 0, 1):
        acc += np.roll(np.roll(m_raw, dy, axis=0), dx, axis=1)
m_raw = acc >= 4


def dilate(mask, iters=1):
    out = mask
    for _ in range(iters):
        acc = np.zeros_like(out, dtype=np.int16)
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                acc += np.roll(np.roll(out, dy, axis=0), dx, axis=1)
        out = acc > 0
    return out


def erode(mask, iters=1):
    out = mask
    for _ in range(iters):
        acc = np.zeros_like(out, dtype=np.int16)
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                acc += np.roll(np.roll(out, dy, axis=0), dx, axis=1)
        out = acc >= 9
    return out


# Morphological closing: a contact's comb is often split into 2+ blobs by the
# LED in the middle and by the traces crossing it. Closing rejoins them so a
# single contact is a single component again.
m = erode(dilate(m_raw, 4), 4)
print('gold px raw:', int(m_raw.sum()), ' closed:', int(m.sum()))

# white silicone membrane (D-pad): bright, desaturated
mx = a.max(2)
mn = a.min(2)
white = (mn > 150) & ((mx - mn) < 28)


def components(mask, min_px, raw=None):
    """Connected components of `mask`; pixel counts taken from `raw` if given."""
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
        py = np.array([p[0] for p in pts]); px = np.array([p[1] for p in pts])
        x0, y0, x1, y1 = int(px.min()), int(py.min()), int(px.max()), int(py.max())
        # real gold coverage inside the closed bbox (the comb, not the closing fat)
        n = int(raw[y0:y1 + 1, x0:x1 + 1].sum()) if raw is not None else len(pts)
        out.append(dict(cx=int(px.mean()), cy=int(py.mean()), n=n,
                        w=int(x1 - x0), h=int(y1 - y0), x0=x0, y0=y0, x1=x1, y1=y1))
    return out


comps = components(m, 400, raw=m_raw)
print('closed components >=400px:', len(comps))


def merge_nearby(cs, gap=34, max_span=250):
    """Rejoin pieces of one contact that closing could not bridge.

    Some contacts (the thin-comb style, e.g. SW13) end up as a top and a bottom
    half separated by ~25px, because the LED and the crossing traces cut the
    ring. Nothing in a single blob is wrong there - they are just split.
    Iteratively union blobs whose bboxes are within `gap`.
    """
    cs = [dict(c) for c in cs]
    merged = True
    while merged:
        merged = False
        for i in range(len(cs)):
            for j in range(i + 1, len(cs)):
                a, b = cs[i], cs[j]
                dx = max(0, max(a['x0'], b['x0']) - min(a['x1'], b['x1']))
                dy = max(0, max(a['y0'], b['y0']) - min(a['y1'], b['y1']))
                if dx > gap or dy > gap:
                    continue
                x0 = min(a['x0'], b['x0']); x1 = max(a['x1'], b['x1'])
                y0 = min(a['y0'], b['y0']); y1 = max(a['y1'], b['y1'])
                if (x1 - x0) > max_span or (y1 - y0) > max_span:
                    continue
                cs[i] = dict(cx=(x0 + x1) // 2, cy=(y0 + y1) // 2,
                             w=x1 - x0, h=y1 - y0, x0=x0, y0=y0, x1=x1, y1=y1,
                             n=int(m_raw[y0:y1 + 1, x0:x1 + 1].sum()))
                cs.pop(j)
                merged = True
                break
            if merged:
                break
    return cs


comps = merge_nearby(comps)
print('after merging split halves:', len(comps))

pads = []
print('\n--- all merged blobs, with verdict ---')
for c in comps:
    ar = c['w'] / max(1, c['h'])
    fill = c['n'] / float(max(1, c['w'] * c['h']))
    why = []
    if not (55 <= c['w'] <= 240 and 55 <= c['h'] <= 240):
        why.append(f"size {c['w']}x{c['h']} out of 55..240")
    if not (0.55 < ar < 1.85):
        why.append(f'aspect {ar:.2f} out of 0.55..1.85')
    if not (0.20 < fill < 0.62):
        why.append(f'fill {fill:.2f} out of 0.20..0.62')
    print(f"  ({c['cx']:4d},{c['cy']:4d}) {c['w']:3d}x{c['h']:3d} fill={fill:.3f} n={c['n']:5d} "
          f"{'KEEP' if not why else 'DROP: ' + '; '.join(why)}")
    if not why:
        c['fill'] = round(fill, 3)
        pads.append(c)

pads.sort(key=lambda p: (p['cy'] // 90, p['cx']))
print(f'\n=== {len(pads)} interdigitated contacts ===')
for i, p in enumerate(pads):
    print(f"  #{i:<2} ({p['cx']:4d},{p['cy']:4d})  {p['w']}x{p['h']}  fill={p['fill']}  n={p['n']}")

json.dump(pads, open('hardware_ref/xbox_pcb/btnboard_contacts.json', 'w'), indent=1)

# ── annotate ──
SC = 0.62
big = im.resize((int(W0 * SC), int(H0 * SC)), Image.LANCZOS)
d = ImageDraw.Draw(big)
try:
    fb = ImageFont.truetype('C:/Windows/Fonts/consolab.ttf', 34)
    fs = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 26)
except Exception:
    fb = fs = ImageFont.load_default()

# shade the silicone membrane area.
# NOTE: auto-detecting "white" here does NOT work - the photo's white backdrop
# and the reflection on the table are white too, and the box ends up framing the
# whole image. The membrane box is therefore a hand-measured constant.
mx0, my0, mx1, my1 = 410, 800, 1020, 1400
d.rectangle([mx0 * SC, my0 * SC, mx1 * SC, my1 * SC], outline=(255, 60, 60), width=5)
d.text((mx0 * SC + 8, my0 * SC + 8), '白色橡胶垫', fill=(255, 60, 60), font=fs)
d.text((mx0 * SC + 8, my0 * SC + 8 + 32), '4 个十字键触点压在这下面',
       fill=(255, 60, 60), font=fs)

for i, p in enumerate(pads):
    x, y = p['cx'] * SC, p['cy'] * SC
    rr = max(p['w'], p['h']) * SC / 2 + 6
    d.ellipse([x - rr, y - rr, x + rr, y + rr], outline=(0, 200, 255), width=4)
    d.text((x - rr - 34, y - 16), str(i), fill=(0, 0, 0), font=fb)
    d.text((x - rr - 36, y - 18), str(i), fill=(0, 200, 255), font=fb)

big.save('hardware_ref/xbox_pcb/btnboard_contacts_map.png')
print(f'\n-> hardware_ref/xbox_pcb/btnboard_contacts_map.png {big.size}')
