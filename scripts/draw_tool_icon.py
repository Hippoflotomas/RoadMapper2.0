import math
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

S = 1024  # work size; final 128

def lerp(a, b, t): return tuple(int(a[i] + (b[i]-a[i]) * t) for i in range(len(a)))

def layer(): return Image.new("RGBA", (S, S), (0, 0, 0, 0))

# ---------------- stake with pennant (behind) ----------------
stake = layer()
d = ImageDraw.Draw(stake)
top = np.array([700.0, 110.0]); bot = np.array([300.0, 930.0])
axis = (bot - top) / np.linalg.norm(bot - top); perp = np.array([-axis[1], axis[0]])
w = 30.0
# wood: many thin strips across the width for a rounded look
steps = 24
for i in range(steps):
    t0 = -w/2 + w*i/steps; t1 = -w/2 + w*(i+1)/steps
    shade = math.cos((i+0.5)/steps*math.pi - math.pi/2)  # 0..1..0
    col = lerp((92, 58, 30), (176, 122, 70), shade**0.8)
    tipLen = 60
    a = top + perp*t0; b = top + perp*t1
    c = bot - axis*tipLen + perp*t1; e = bot - axis*tipLen + perp*t0
    d.polygon([tuple(a), tuple(b), tuple(c), tuple(e)], fill=col + (255,))
# pointed tip
tip_base_l = bot - axis*60 + perp*(-w/2); tip_base_r = bot - axis*60 + perp*(w/2)
d.polygon([tuple(tip_base_l), tuple(tip_base_r), tuple(bot)], fill=(120, 80, 44, 255))
d.polygon([tuple(tip_base_l), tuple(bot - axis*60), tuple(bot)], fill=(146, 100, 58, 255))
# round cap
d.ellipse([top[0]-w/2, top[1]-w/2, top[0]+w/2, top[1]+w/2], fill=(160, 110, 62, 255))

# pennant: swallowtail, flying to the right from the upper pole
p1 = top + axis*20; p2 = top + axis*230
tip_hi = np.array([p1[0] + 330, p1[1] + 70]); tip_lo = np.array([p2[0] + 300, p2[1] + 10])
notch = (tip_hi + tip_lo) / 2 + np.array([-90, 0])
pen = [tuple(p1), tuple(tip_hi), tuple(notch), tuple(tip_lo), tuple(p2)]
pmask = Image.new("L", (S, S), 0); ImageDraw.Draw(pmask).polygon(pen, fill=255)
# gradient purple: lighter near the pole, a fold shadow band
g = np.zeros((S, S, 4), np.uint8)
xs = np.arange(S)[None, :].repeat(S, 0).astype(float)
ys = np.arange(S)[:, None].repeat(S, 1).astype(float)
t = np.clip((xs - p1[0]) / 330.0, 0, 1)
fold = 0.18 * np.sin(t * math.pi * 2.2)
base = np.array([132, 70, 170]); dark = np.array([70, 30, 100])
k = np.clip(1 - 0.55*t + fold, 0, 1)[..., None]
g[..., :3] = (dark + (base - dark) * k).astype(np.uint8)
g[..., 3] = np.array(pmask)
pennant = Image.fromarray(g, "RGBA")
# highlight stripe along the top edge
hl = layer(); ImageDraw.Draw(hl).line([tuple(p1 + np.array([6, 10])), tuple(tip_hi + np.array([-30, 8]))], fill=(200, 150, 225, 200), width=12)
hl.putalpha(Image.fromarray(np.minimum(np.array(hl.split()[3]), np.array(pmask))))
stake = Image.alpha_composite(stake, pennant)
stake = Image.alpha_composite(stake, hl)

# ---------------- scroll (front), drawn level then rotated ----------------
sc = layer(); sd = ImageDraw.Draw(sc)
x0, x1, y0, y1 = 250, 790, 432, 612
h = y1 - y0; capw = 70
body = np.zeros((S, S, 4), np.uint8)
for y in range(y0, y1):
    v = (y - y0) / h
    shade = math.sin(v * math.pi) ** 0.7          # cylinder: bright middle
    shade = shade * (1.0 - 0.35 * v)              # lit from above
    col = lerp((150, 108, 60), (246, 226, 180), shade)
    body[y, x0:x1] = col + (255,)
bodyImg = Image.fromarray(body, "RGBA")
# left end: the curved outer edge of the roll
cap = layer(); cd = ImageDraw.Draw(cap)
cd.ellipse([x0 - capw/2, y0, x0 + capw/2, y1], fill=(214, 184, 130, 255))
sc = Image.alpha_composite(sc, cap)
sc = Image.alpha_composite(sc, bodyImg)
sd = ImageDraw.Draw(sc)
# right end: open roll with a spiral
sd.ellipse([x1 - capw/2, y0, x1 + capw/2, y1], fill=(226, 198, 146, 255))
cx, cy = x1, (y0 + y1) / 2
pts = []
for i in range(220):
    a = i / 220 * math.pi * 5.2
    r = 6 + i / 220 * (h / 2 - 12)
    pts.append((cx + math.cos(a) * r * (capw / h), cy + math.sin(a) * r))
sd.line(pts, fill=(128, 88, 48, 255), width=10)
# map drawing on the body: dotted road with a marker at its end
road = [(330, 560), (390, 520), (450, 545), (520, 495), (600, 520), (670, 480)]
for i in range(len(road) - 1):
    ax, ay = road[i]; bx, by = road[i + 1]
    for s in np.linspace(0, 1, 5, endpoint=False):
        px, py = ax + (bx - ax) * s, ay + (by - ay) * s
        sd.ellipse([px - 10, py - 10, px + 10, py + 10], fill=(120, 76, 40, 255))
mx, my = 700, 468
sd.ellipse([mx - 28, my - 28, mx + 28, my + 28], fill=(120, 60, 160, 255))
sd.ellipse([mx - 11, my - 11, mx + 11, my + 11], fill=(236, 214, 250, 255))
# twine band
for yy in range(y0 - 4, y1 + 4):
    v = (yy - y0) / h
    col = lerp((70, 44, 24), (150, 104, 60), max(0, math.sin(v * math.pi)))
    sd.line([(452, yy), (486, yy)], fill=col + (255,))
sc = sc.rotate(28, resample=Image.BICUBIC, center=(S / 2, S / 2 + 40))

# ---------------- compose, outline, downscale ----------------
art = Image.alpha_composite(stake, sc)
alpha = art.split()[3].point(lambda a: 255 if a > 40 else 0)
outline_a = alpha.filter(ImageFilter.MaxFilter(25))
outline = Image.new("RGBA", (S, S), (38, 24, 14, 255)); outline.putalpha(outline_a)
# soft drop shadow
shadow_a = outline_a.filter(ImageFilter.GaussianBlur(18)).point(lambda a: int(a * 0.45))
shadow = Image.new("RGBA", (S, S), (0, 0, 0, 255)); shadow.putalpha(shadow_a)
shadow = shadow.transform((S, S), Image.AFFINE, (1, 0, -14, 0, 1, -18))
final = Image.alpha_composite(Image.alpha_composite(layer(), shadow), outline)
final = Image.alpha_composite(final, art)
# crop to content with a margin, square, then downscale
bbox = final.getbbox()
cx, cy = (bbox[0] + bbox[2]) / 2, (bbox[1] + bbox[3]) / 2
half = max(bbox[2] - bbox[0], bbox[3] - bbox[1]) / 2 * 1.04
final = final.crop((int(cx - half), int(cy - half), int(cx + half), int(cy + half)))
final.resize((128, 128), Image.LANCZOS).save("ToolIcon.png")
# preview on a dark and a light background, 128 and 64
prev = Image.new("RGBA", (128 * 2 + 64 * 2 + 50, 140), (54, 46, 40, 255))
icon = Image.open("ToolIcon.png")
prev.alpha_composite(icon, (6, 6))
light = Image.new("RGBA", (128, 128), (200, 190, 170, 255)); light.alpha_composite(icon); prev.alpha_composite(light, (144, 6))
small = icon.resize((64, 64), Image.LANCZOS); prev.alpha_composite(small, (282, 6))
prev.save("preview.png")
print("ok")
