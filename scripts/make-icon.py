#!/usr/bin/env python3
"""Draws the DevSweep app icon and writes App/Assets.xcassets/AppIcon.appiconset.

    python3 scripts/make-icon.py

Needs Pillow (pip install pillow). A rounded square in macOS's icon shape with
a before-and-after pair of storage bars: the red segment on top is gone below,
leaving freed space outlined in cyan.
"""
import json, math, os
from PIL import Image, ImageDraw, ImageFilter

S = 2048                      # draw big, shrink for smooth edges
OUT = os.path.join(os.path.dirname(__file__), "..", "App", "Assets.xcassets", "AppIcon.appiconset")

def lerp(a, b, t): return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))

def gradient():
    top, bottom = (44, 46, 66), (24, 25, 38)
    img = Image.new("RGB", (S, S))
    px = img.load()
    for y in range(S):
        for x in range(S):
            px[x, y] = lerp(top, bottom, min(1, (0.8 * y + 0.2 * x) / S))
    return img

def squircle_mask():
    body, margin = int(S * 0.805), int(S * 0.0975)
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).rounded_rectangle([margin, margin, margin + body, margin + body], radius=int(body * 0.225), fill=255)
    return m

def art():
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    W, H = S * 0.60, S * 0.125
    x0 = (S - W) / 2
    gap, rad = S * 0.012, S * 0.028
    RED, GREY, FREE, CYAN = (240, 85, 60, 255), (142, 142, 147, 255), (74, 74, 94, 255), (40, 210, 220, 255)

    def blocks(y, parts):
        """parts: (share of the bar, fill, outline) left to right, as separate rounded blocks."""
        x = x0
        for share, fill, outline in parts:
            w = W * share - gap
            box = [x, y, x + w, y + H]
            if fill: d.rounded_rectangle(box, radius=rad, fill=fill)
            if outline: d.rounded_rectangle(box, radius=rad, outline=outline, width=int(S * 0.009))
            x += W * share

    # Before: used space with a big red part, a little free space
    yb = S * 0.285
    blocks(yb, [(0.26, RED, None), (0.50, GREY, None), (0.24, FREE, None)])
    # Arrow: the red part is gone
    cx, ay = S * 0.5, S * 0.505
    aw = S * 0.045
    d.rectangle([cx - aw, ay - S * 0.045, cx + aw, ay + S * 0.012], fill=(255, 255, 255, 255))
    d.polygon([(cx - S * 0.1, ay + S * 0.006), (cx + S * 0.1, ay + S * 0.006), (cx, ay + S * 0.10)], fill=(255, 255, 255, 255))
    # After: same grey, the freed space shown as a bright outlined block
    ya = S * 0.625
    blocks(ya, [(0.26, (40, 210, 220, 60), CYAN), (0.50, GREY, None), (0.24, FREE, None)])
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
