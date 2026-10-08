#!/usr/bin/env python3
"""Draws the DevSweep app icon and writes App/Assets.xcassets/AppIcon.appiconset.

    python3 scripts/make-icon.py

Needs Pillow and numpy (pip install pillow numpy). A disk-usage ring on a white
rounded square: the used part blue to violet, the freed part mint, the free part
pale lavender, with a soft coloured shadow.
"""
import json, os
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "App", "Assets.xcassets", "AppIcon.appiconset")
DOCS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "docs", "icon.png")

S = 2048
c = S / 2
R, TH = S * .31, S * .105

yy, xx = np.mgrid[0:S, 0:S]
ang = (np.degrees(np.arctan2(yy - c, xx - c)) + 90) % 360       # 0 = 12 o'clock, clockwise
rad = np.hypot(xx - c, yy - c)

def ring_mask(a0, a1, gap=1.2, round_px=S * .0055):
    inside = (rad <= R) & (rad >= R - TH)
    arc = (ang >= a0 + gap / 2) & (ang <= a1 - gap / 2)
    m = Image.fromarray(((inside & arc) * 255).astype(np.uint8))
    m = m.filter(ImageFilter.GaussianBlur(round_px)).point(lambda v: 255 if v > 140 else 0)   # softly rounded ends
    return m.filter(ImageFilter.GaussianBlur(1.2))

def conic(stops, a0, a1):
    t = np.clip((ang - a0) / (a1 - a0), 0, 1)
    cols = np.array(stops, dtype=float)
    pos = np.linspace(0, 1, len(stops))
    out = np.stack([np.interp(t, pos, cols[:, k]) for k in range(3)], axis=-1)
    return Image.fromarray(out.astype(np.uint8)).convert("RGBA")

def layer(a0, a1, stops, alpha=255):
    img = conic(stops, a0, a1)
    m = ring_mask(a0, a1)
    if alpha != 255: m = m.point(lambda v: int(v * alpha / 255))
    img.putalpha(m)
    return img

# arcs, degrees clockwise from 12 o'clock
USED_END, FREED_END = 220, 290          # used 0-220, freed 220-290, free 290-360
used  = layer(0, USED_END, [(0, 170, 250), (40, 120, 255), (88, 70, 245), (150, 60, 235)])
freed = layer(USED_END, FREED_END, [(0, 214, 168), (0, 206, 200), (0, 192, 226)])
free  = layer(FREED_END, 360, [(206, 208, 232), (198, 200, 226)], alpha=255)
art = Image.alpha_composite(Image.alpha_composite(free, used), freed)

def glow(src, radius, strength):
    g = src.filter(ImageFilter.GaussianBlur(radius))
    a = g.split()[3].point(lambda v: int(min(255, v * strength)))
    g.putalpha(a); return g
base = Image.new("RGBA", (S, S), (0, 0, 0, 0))
# bland background: one calm dark navy, a whisper of a gradient
bg = Image.new("RGB", (S, S)); px = bg.load()
for y in range(S):
    t = y / S
    row = (int(255 - 9 * t), int(255 - 9 * t), int(255 - 5 * t))
    for x in range(S): px[x, y] = row
bg = bg.convert("RGBA")
sh = glow(art, S * .03, .55); sh = sh.transform(sh.size, Image.AFFINE, (1, 0, 0, 0, 1, -S * .018)); bg = Image.alpha_composite(bg, sh)
bg = Image.alpha_composite(bg, art)
# faint glassy sheen on the ring: a soft white wash on the upper-left of the used arc
sheen = Image.new("RGBA", (S, S), (255, 255, 255, 0))
ImageDraw.Draw(sheen).ellipse([c - R, c - R - S * .06, c + R * .1, c + R * .05], fill=(255, 255, 255, 38))
sm = used.split()[3]
sheen.putalpha(Image.composite(sheen.split()[3], Image.new("L", (S, S), 0), sm))
bg = Image.alpha_composite(bg, sheen)

body, m = int(S * .805), int(S * .0975)
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([m, m, m + body, m + body], radius=int(body * .225), fill=255)
icon = Image.new("RGBA", (S, S), (0, 0, 0, 0)); icon.paste(bg, (0, 0), mask)
drop = Image.new("RGBA", (S, S), (0, 0, 0, 0)); drop.putalpha(mask.filter(ImageFilter.GaussianBlur(S * .01)).point(lambda v: int(v * .28)))
drop = drop.transform(drop.size, Image.AFFINE, (1, 0, 0, 0, 1, -S * .01))
final = Image.alpha_composite(drop, icon)
edge = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(edge).rounded_rectangle([m, m, m + body, m + body], radius=int(body * .225), outline=(0, 0, 0, 34), width=6)
final = Image.alpha_composite(final, edge)

os.makedirs(OUT, exist_ok=True)
images = []
for pt in (16, 32, 128, 256, 512):
    for scale in (1, 2):
        px = pt * scale
        name = f"icon_{pt}x{pt}{'@2x' if scale == 2 else ''}.png"
        final.resize((px, px), Image.LANCZOS).save(os.path.join(OUT, name))
        images.append({"idiom": "mac", "size": f"{pt}x{pt}", "scale": f"{scale}x", "filename": name})
json.dump({"images": images, "info": {"version": 1, "author": "xcode"}}, open(os.path.join(OUT, "Contents.json"), "w"), indent=2)
final.resize((1024, 1024), Image.LANCZOS).save(DOCS)
