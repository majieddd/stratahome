"""Draws the StrataHome mark (four sediment layers on a dark squircle) and writes assets/app.ico + assets/icon-512.png.
Needs Pillow:  python assets/make_icon.py"""
from PIL import Image, ImageDraw
import os

def mark(size, bands=((232, 176, 75), (214, 150, 80), (96, 168, 150), (63, 140, 160)), bg=(20, 24, 28)):
    s = size * 4                                     # draw 4x, then downsample for clean edges
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle((0, 0, s - 1, s - 1), radius=int(s * 0.22), fill=bg + (255,))
    pad, gap = s * 0.19, s * 0.045
    bh = (s - 2 * pad - 3 * gap) / 4
    inset = (0.0, 0.07, 0.03, 0.10)                  # each layer ends a little differently, like real strata
    for i, c in enumerate(bands):
        y0 = pad + i * (bh + gap)
        x0, x1 = pad + s * inset[i] * 0.6, s - pad - s * inset[(i + 2) % 4] * 0.9
        d.rounded_rectangle((x0, y0, x1, y0 + bh), radius=int(bh * 0.42), fill=c + (255,))
    return im.resize((size, size), Image.LANCZOS)

here = os.path.dirname(os.path.abspath(__file__))
sizes = [16, 24, 32, 48, 64, 128, 256]
mark(256).save(os.path.join(here, "app.ico"), sizes=[(n, n) for n in sizes])
mark(512).save(os.path.join(here, "icon-512.png"))
print("wrote app.ico + icon-512.png")
