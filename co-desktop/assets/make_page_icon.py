# SPDX-License-Identifier: AGPL-3.0-or-later
# CO Desktop 0.9.2: icon of the window of a module page (src/CO.Desktop/co-page.ico, ext/1 ui.window): a page with a
# pencil on the CO tile.
# Run: python assets/make_page_icon.py (Pillow).
from pathlib import Path
from PIL import Image, ImageDraw

S = 1024  # drawn at 2x, scaled down for smooth edges
img = Image.new('RGBA', (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
# tile: teal gradient (page window) instead of the blue of co.ico
for y in range(32, S - 32):
    t = (y - 32) / (S - 64)
    c = (int(22 + (8 - 22) * t), int(160 + (92 - 160) * t), int(150 + (96 - 150) * t), 255)
    d.line([(32, y), (S - 32, y)], fill=c)
mask = Image.new('L', (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([32, 32, S - 32, S - 32], radius=224, fill=255)
img.putalpha(mask)
d = ImageDraw.Draw(img)
# page with a folded corner
d.polygon([(250, 170), (600, 170), (760, 330), (760, 860), (250, 860)], fill='white')
d.polygon([(600, 170), (600, 330), (760, 330)], fill=(190, 230, 226, 255))
for i, y in enumerate((440, 540, 640, 740)):
    d.rounded_rectangle([330, y, 330 + (360 if i % 2 == 0 else 260), y + 36], radius=18, fill=(150, 190, 188, 255))
# pencil (orange, the agent's colour in co-browser.ico), diagonal over the page
pencil = Image.new('RGBA', (S, S), (0, 0, 0, 0))
p = ImageDraw.Draw(pencil)
p.rectangle([470, 380, 590, 860], fill=(255, 159, 46, 255))
p.rectangle([470, 300, 590, 380], fill=(240, 96, 96, 255))
p.polygon([(470, 860), (590, 860), (530, 980)], fill=(255, 222, 170, 255))
p.polygon([(510, 940), (550, 940), (530, 980)], fill=(60, 60, 60, 255))
pencil = pencil.rotate(-40, center=(530, 640), resample=Image.BICUBIC)
pencil = pencil.transform((S, S), Image.AFFINE, (1, 0, -150, 0, 1, -20), resample=Image.BICUBIC)
img.alpha_composite(pencil)
out = Path(__file__).resolve().parents[1] / 'src' / 'CO.Desktop' / 'co-page.ico'
img.resize((256, 256), Image.LANCZOS).save(out, sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
img.resize((256, 256), Image.LANCZOS).save(Path(__file__).with_name('page-icon-256.png'))
print(out)
