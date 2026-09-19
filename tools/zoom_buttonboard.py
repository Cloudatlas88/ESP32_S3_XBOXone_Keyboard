"""
Zoom the regions of the button board that matter for "where do I solder the D-pad".

Region A is the one that answers the question, so it gets its own readable image.
Regions B/C (contact structure) are written separately.
All boxes are in PREVIEW coordinates (the 923px-wide view I saw), auto-scaled.
"""
from PIL import Image, ImageDraw, ImageFont

SRC = 'hardware_ref/xbox_pcb/3_按键板.jpg'
im = Image.open(SRC).convert('RGB')
S = im.width / 923.0
print(f'source {im.size}, scale x{S:.3f}')


def crop_pv(box, z):
    x0, y0, x1, y1 = (int(v * S) for v in box)
    c = im.crop((x0, y0, x1, y1))
    return c.resize((c.width * z, c.height * z), Image.LANCZOS), (x0, y0, x1, y1)


try:
    fs = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 30)
except Exception:
    fs = ImageFont.load_default()


def save(name, box, z, title):
    c, ob = crop_pv(box, z)
    out = Image.new('RGB', (c.width, c.height + 44), (255, 255, 255))
    out.paste(c, (0, 44))
    ImageDraw.Draw(out).text((8, 6), title, fill=(0, 0, 0), font=fs)
    out.save(name)
    print(f'  {name}: orig box {ob} -> {out.size}')
    return out.size


# A: the whole white D-pad / bottom-left area  (the actual question)
save('hardware_ref/xbox_pcb/z_dpad_area.png', (130, 270, 430, 600), 4,
     'A 白色十字键所在的板区域')

# B: one gold circular "spoke" contact, big
save('hardware_ref/xbox_pcb/z_contact.png', (388, 138, 472, 222), 9,
     'B 金色圆形触点（辐条状）特写')

# C: one black SMD switch, big
save('hardware_ref/xbox_pcb/z_switch.png', (252, 148, 352, 244), 9,
     'C 黑色贴片开关特写')
