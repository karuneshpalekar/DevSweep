#!/usr/bin/env python3
"""Draws the DevSweep app icon and writes App/Assets.xcassets/AppIcon.appiconset.

    python3 scripts/make-icon.py

Needs Pillow (pip install pillow). A rounded square in macOS's icon shape with
a disk-usage ring with a grey used part and a cyan freed part.
"""
import json, math, os
from PIL import Image, ImageDraw, ImageFilter

S = 2048                      # draw big, shrink for smooth edges
OUT = os.path.join(os.path.dirname(__file__), "..", "App", "Assets.xcassets", "AppIcon.appiconset")

def lerp(a, b, t): return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))

def gradient():
    top, bottom = (46, 44, 90), (22, 24, 44)
    img = Image.new("RGB", (S, S))
    px = img.load()
    for y in range(S):
        for x in range(S):
            px[x, y] = lerp(top, bottom, min(1, (0.5 * y + 0.5 * x) / S))
    return img

def squircle_mask():
    body, margin = int(S * 0.805), int(S * 0.0975)
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).rounded_rectangle([margin, margin, margin + body, margin + body], radius=int(body * 0.225), fill=255)
    return m

def ring(layer, cx, cy, R, thick, start, end, fill):
    """A donut slice from `start` to `end` degrees (0 = 3 o'clock, clockwise)."""
    t = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(t).pieslice([cx - R, cy - R, cx + R, cy + R], start, end, fill=fill)
    hole = Image.new("L", (S, S), 0)
    ImageDraw.Draw(hole).ellipse([cx - R + thick, cy - R + thick, cx + R - thick, cy + R - thick], fill=255)
    a = t.split()[3]
    a = Image.composite(Image.new("L", (S, S), 0), a, hole)
    t.putalpha(a)
    return Image.alpha_composite(layer, t)

def art():
    """A disk-usage ring: free (dark), used (grey) and the freed part (cyan)."""
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    cx = cy = S / 2
    R, th = S * 0.30, S * 0.095
    layer = ring(layer, cx, cy, R, th, -90, 270, (74, 74, 100, 255))   # whole ring: free
    layer = ring(layer, cx, cy, R, th, -90, 130, (150, 150, 158, 255))  # used
    layer = ring(layer, cx, cy, R, th, 130, 200, (36, 214, 224, 255))   # freed
    return layer

def main():
    base = gradient().convert("RGBA")
    mask = squircle_mask()
    # soft top highlight
    hi = Image.new("RGBA", (S, S), (255, 255, 255, 0))
    ImageDraw.Draw(hi).ellipse([-S * 0.2, -S * 0.55, S * 1.2, S * 0.45], fill=(255, 255, 255, 0))
    base = Image.alpha_composite(base, hi)
    layer = art()
    shadow = layer.split()[3].filter(ImageFilter.GaussianBlur(S * 0.012)).point(lambda v: int(v * 0.28))
    sh = Image.new("RGBA", (S, S), (0, 40, 90, 0)); sh.putalpha(shadow)
    sh = sh.transform(sh.size, Image.AFFINE, (1, 0, 0, 0, 1, -S * 0.008))
    base = Image.alpha_composite(base, sh)
    base = Image.alpha_composite(base, layer)
    icon = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    icon.paste(base, (0, 0), mask)
    # drop shadow under the whole icon, like system icons
    drop = Image.new("RGBA", (S, S), (0, 0, 0, 0)); drop.putalpha(mask.filter(ImageFilter.GaussianBlur(S * 0.01)).point(lambda v: int(v * 0.35)))
    drop = drop.transform(drop.size, Image.AFFINE, (1, 0, 0, 0, 1, -S * 0.01))
    final = Image.alpha_composite(drop, icon)

    os.makedirs(OUT, exist_ok=True)
    images = []
    for pt in (16, 32, 128, 256, 512):
        for scale in (1, 2):
            px = pt * scale
            name = f"icon_{pt}x{pt}{'@2x' if scale == 2 else ''}.png"
            final.resize((px, px), Image.LANCZOS).save(os.path.join(OUT, name))
            images.append({"idiom": "mac", "size": f"{pt}x{pt}", "scale": f"{scale}x", "filename": name})
    json.dump({"images": images, "info": {"version": 1, "author": "xcode"}}, open(os.path.join(OUT, "Contents.json"), "w"), indent=2)
    final.resize((1024, 1024), Image.LANCZOS).save(os.path.join(os.path.dirname(OUT), "..", "..", "docs", "icon.png"))

main()
