"""
Trace each leader line from its endpoint dot back to the text label it belongs to.

Why this exists: the endpoint->button-name table was written when the diagram
could not be viewed, so the names were inferred from coordinates alone. Watching the rendered overlay shows the right-hand leaders cross
each other, so eyeballing is unreliable. This connects them programmatically.

Method: build the blue leader-line mask, flood-fill the component containing each
dot, then take the point of that component farthest from the dot (the label end)
and match it to the nearest text-label box.
"""
import json
import numpy as np
from PIL import Image
from collections import deque

SRC = 'host/KbConfigurator/Assets/xbox-controller.png'
RAW = json.load(open('tools/xbox_diagram_raw.json'))

im = Image.open(SRC).convert('RGB')
W, H = im.size
a = np.asarray(im).astype(np.int16)
r, g, b = a[:, :, 0], a[:, :, 1], a[:, :, 2]

# leader lines are a pure cyan-blue; ground/table is not
blue = (b > 150) & (b - r > 60) & (g > 120) & (g < 220) & (r < 130)
print(f'blue px = {int(blue.sum())}')

# Eroding the mask disconnects thin leaders from their dots, which silently
# produced "far end ~13px from the dot" garbage for 8 of the 15 dots. Trace on
# the FULL mask instead; dot-to-dot merge is not a real risk (dots are >=100px
# apart, lines are ~4px).
thin = blue
print(f'tracing mask px = {int(thin.sum())}')

# Names of the text-label boxes, read off the rendered overlay
# (hardware_ref/xbox_pcb/xbox_diagram_dots.png). Index = position in the
# n>=6-filtered list, which is what the tracing code uses.
LABEL_NAMES = [
    'Xbox button', 'Left bumper (LB)', 'Right bumper (RB)',
    'Left trigger (LT)', 'Right trigger (RT)', 'Y button',
    'Left stick', 'B button', 'X button', 'View button', 'A button',
    'Directional Pad', '(D-pad)', 'Menu button', 'Right Stick', 'Share button',
]


def flood(mask, sy, sx):
    seen = np.zeros_like(mask, dtype=bool)
    if not mask[sy, sx]:
        # snap to the nearest masked pixel within 20px
        best = None
        for dy in range(-20, 21):
            for dx in range(-20, 21):
                ny, nx = sy + dy, sx + dx
                if 0 <= ny < H and 0 <= nx < W and mask[ny, nx]:
                    dd = dy * dy + dx * dx
                    if best is None or dd < best[0]:
                        best = (dd, ny, nx)
        if best is None:
            return []
        sy, sx = best[1], best[2]
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
    return pts


labels = [lb for lb in RAW['labels'] if lb['n'] >= 6]
print(f'{len(labels)} text-label boxes (n>=6)\n')

print(f'{"dot":>4} {"dot px":>14} {"far end px":>14} {"lines?":>7}  nearest label boxes')
print('-' * 92)

result = []
for i, dot in enumerate(RAW['dots']):
    dx0, dy0 = dot['px']
    pts = flood(thin, dy0, dx0)
    if not pts:
        print(f'{i:>4}  (no line found)')
        result.append(None)
        continue

    # how many dots does this component touch?  >1 means leaders crossed/merged
    ys = np.array([p[0] for p in pts]); xs = np.array([p[1] for p in pts])
    touched = [j for j, o in enumerate(RAW['dots'])
               if ((xs - o['px'][0]) ** 2 + (ys - o['px'][1]) ** 2).min() < 45]

    d2 = (xs - dx0) ** 2 + (ys - dy0) ** 2
    k = int(np.argmax(d2))
    fx, fy = int(xs[k]), int(ys[k])

    # rank the label boxes by distance to the far end
    scored = []
    for lb in labels:
        lx0, ly0, lx1, ly1 = lb['px']
        # distance from the far end to the box's rectangle
        ddx = max(lx0 - fx, 0, fx - lx1)
        ddy = max(ly0 - fy, 0, fy - ly1)
        scored.append((ddx * ddx + ddy * ddy, lb))
    scored.sort(key=lambda t: t[0])
    top = scored[:3]

    print(f'{i:>4}  {str((dx0,dy0)):>14}  {str((fx,fy)):>14}  {len(touched):>7}  ' +
          ' | '.join(f'{LABEL_NAMES[labels.index(lb)]}(d={dd**0.5:.0f})' for dd, lb in top))
    result.append(dict(dot=i, far=(fx, fy), touched=touched,
                       best=labels.index(top[0][1]),
                       best_label=LABEL_NAMES[labels.index(top[0][1])],
                       bestdist=round(top[0][0] ** 0.5, 1)))

json.dump(result, open('tools/xbox_dot_labels.json', 'w'), indent=1)
print('\n-> tools/xbox_dot_labels.json')
