"""Zoom regions of the rendered UI screenshot so the hotspot layer can be judged."""
import sys
from PIL import Image, ImageDraw, ImageFont

SRC = sys.argv[1] if len(sys.argv) > 1 else 'hardware_ref/ui_shot_layout.png'
im = Image.open(SRC).convert('RGB')
print('shot', im.size)

REGIONS = [
    ('右：ABXY 菱形 + View/Menu + 右摇杆', (520, 130, 1010, 460), 2.6),
    ('左：LB / 左摇杆 / 十字键 + 徽章',    (300, 130, 700, 470), 2.6),
]

tiles = []
for name, box, z in REGIONS:
    c = im.crop(box)
    c = c.resize((int(c.width * z), int(c.height * z)), Image.LANCZOS)
    tiles.append((name, c))
    print(f'  {name}: {box} -> {c.size}')

try:
    fs = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 30)
except Exception:
    fs = ImageFont.load_default()

pad, lab = 14, 40
W = max(t[1].width for t in tiles) + pad * 2
H = sum(t[1].height + lab + pad for t in tiles) + pad
out = Image.new('RGB', (W, H), (255, 255, 255))
d = ImageDraw.Draw(out)
y = pad
for name, t in tiles:
    d.text((pad, y + 4), name, fill=(0, 0, 0), font=fs)
    y += lab
    out.paste(t, (pad, y))
    d.rectangle([pad - 1, y - 1, pad + t.width, y + t.height], outline=(0, 0, 0), width=2)
    y += t.height + pad

out.save('hardware_ref/ui_shot_zoom.png')
print('-> hardware_ref/ui_shot_zoom.png', out.size)
