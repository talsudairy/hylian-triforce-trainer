"""Cut the shield out of the baked-in checkerboard and build the logo PNGs and ICO."""
import sys
import numpy as np
from PIL import Image, ImageFilter

src, outdir = sys.argv[1], sys.argv[2]
im = Image.open(src).convert("RGB")
a = np.asarray(im).astype(np.int32)
H, W, _ = a.shape
lum = a.mean(2)
sat = a.max(2) - a.min(2)

# Checkerboard pixels: grey (low saturation), dark ~35 or light ~92, or the blur between them.
bgish = (sat < 22) & (lum < 110)

# Flood fill from the border through bg-ish pixels (iterative dilation, no scipy).
bg = np.zeros((H, W), bool)
bg[0, :] = bgish[0, :]; bg[-1, :] = bgish[-1, :]; bg[:, 0] = bgish[:, 0]; bg[:, -1] = bgish[:, -1]
while True:
    g = bg.copy()
    g[1:, :] |= bg[:-1, :]; g[:-1, :] |= bg[1:, :]; g[:, 1:] |= bg[:, :-1]; g[:, :-1] |= bg[:, 1:]
    g &= bgish
    if (g == bg).all():
        break
    bg = g

fg = ~bg
# Shield only: rows above the gap between the shield and the text.
rows = fg.sum(1)
gap = next(y for y in range(700, H) if rows[y] == 0)
text_top = next(y for y in range(gap, H) if rows[y] > 0)
print("shield ends", gap, "text starts", text_top)
# The gold letters enclose checkerboard (the holes in O, A, R): clear all grey in the text band.
fg[gap:, :] &= ~bgish[gap:, :]

def soft_alpha(mask):
    m = Image.fromarray((mask * 255).astype(np.uint8))
    m = m.filter(ImageFilter.MinFilter(3))       # eat the 1px grey fringe
    return m.filter(ImageFilter.GaussianBlur(0.7))

def crop(rgba, pad):
    bbox = rgba.getchannel("A").point(lambda v: 255 if v > 8 else 0).getbbox()
    x0, y0, x1, y1 = bbox
    return rgba.crop((max(0, x0 - pad), max(0, y0 - pad), min(rgba.width, x1 + pad), min(rgba.height, y1 + pad)))

full = im.copy(); full.putalpha(soft_alpha(fg))
logo = crop(full, 4)
logo.save(f"{outdir}/HylianTriforceLogo.png")

shield_mask = fg.copy(); shield_mask[gap:, :] = False
sh = im.copy(); sh.putalpha(soft_alpha(shield_mask))
sh = crop(sh, 2)
# square canvas for the icon
side = max(sh.size)
sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
sq.paste(sh, ((side - sh.width) // 2, (side - sh.height) // 2), sh)
sq.save(f"{outdir}/HylianTriforceShield.png")
sizes = [16, 24, 32, 48, 64, 128, 256]
big = sq.resize((256, 256), Image.LANCZOS)
big.save(f"{outdir}/HylianTriforce.ico", sizes=[(s, s) for s in sizes])
print("logo", logo.size, "shield", sq.size)
