"""
Draw the 15 detected leader-line endpoints onto the controller diagram, numbered.

The naming table was originally derived from coordinates alone
(the image was not viewable when it was written), so it has to be checked against
the actual picture. This overlay makes each dot identifiable at a glance.
"""
import json
import numpy as np
from PIL import Image, ImageDraw, ImageFont

SRC = 'host/KbConfigurator/Assets/xbox-controller.png'
RAW = json.load(open('tools/xbox_diagram_raw.json'))

im = Image.open(SRC).convert('RGB')
d = ImageDraw.Draw(im)
try:
    fb = ImageFont.truetype('C:/Windows/Fonts/consolab.ttf', 46)
    fsm = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 34)
except Exception:
    fb = fsm = ImageFont.load_default()

for i, dot in enumerate(RAW['dots']):
    x, y = dot['px']
    r = 26
    d.ellipse([x - r, y - r, x + r, y + r], outline=(255, 0, 0), width=7)
    lab = str(i)
    d.text((x + r + 10, y - r - 10), lab, fill=(255, 255, 255), font=fb)
    d.text((x + r + 8, y - r - 12), lab, fill=(200, 0, 0), font=fb)

# also mark every plausible text-label box from the raw analysis (green)
for j, lb in enumerate(RAW['labels']):
    if lb['n'] < 6:
        continue
    x0, y0, x1, y1 = lb['px']
    d.rectangle([x0, y0, x1, y1], outline=(0, 160, 0), width=5)
    d.text((x0, y0 - 34), f'L{j}', fill=(0, 120, 0), font=fsm)

im.save('hardware_ref/xbox_pcb/xbox_diagram_dots.png')
print('-> hardware_ref/xbox_pcb/xbox_diagram_dots.png', im.size)
for i, dot in enumerate(RAW['dots']):
    print(f"  dot {i:2d}  px{tuple(dot['px'])}  norm({dot['x']:.4f},{dot['y']:.4f})")
