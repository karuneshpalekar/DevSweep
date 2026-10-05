#!/usr/bin/env python3
"""Draws the DevSweep app icon and writes App/Assets.xcassets/AppIcon.appiconset.

    python3 scripts/make-icon.py

Needs Pillow (pip install pillow). A rounded square in macOS's icon shape, a
blue-to-teal gradient, a white sweeping broom and three sparkles.
"""
import json, math, os
from PIL import Image, ImageDraw, ImageFilter

S = 2048                      # draw big, shrink for smooth edges
OUT = os.path.join(os.path.dirname(__file__), "..", "App", "Assets.xcassets", "AppIcon.appiconset")

def lerp(a, b, t): return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))

def gradient():
    top, bottom = (58, 112, 255), (20, 190, 190)
    img = Image.new("RGB", (S, S))
    px = img.load()
    for y in range(S):
        for x in range(S):
            t = (0.65 * y + 0.35 * x) / S
            px[x, y] = lerp(top, bottom, min(1, t))
    return img

def squircle_mask():
    body, margin = int(S * 0.805), int(S * 0.0975)
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).rounded_rectangle([margin, margin, margin + body, margin + body], radius=int(body * 0.225), fill=255)
    return m

def star(d, cx, cy, r, fill):
    pts = []
    for i in range(8):
        ang = math.pi / 4 * i - math.pi / 2
        rad = r if i % 2 == 0 else r * 0.28
        pts.append((cx + rad * math.cos(ang), cy + rad * math.sin(ang)))
    d.polygon(pts, fill=fill)

def rot(points, cx, cy, deg):
    a = math.radians(deg)
    return [(cx + (x - cx) * math.cos(a) - (y - cy) * math.sin(a), cy + (x - cx) * math.sin(a) + (y - cy) * math.cos(a)) for x, y in points]

def art():
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    cx, cy = S * 0.5, S * 0.52
    # Handle: a long rounded bar, tilted
    hw = S * 0.032
    handle = [(cx - hw, cy - S * 0.33), (cx + hw, cy - S * 0.33), (cx + hw, cy + S * 0.02), (cx - hw, cy + S * 0.02)]
    d.polygon(rot(handle, cx, cy, 32), fill=(255, 255, 255, 255))
    for ex, ey in rot([(cx, cy - S * 0.33), (cx, cy + S * 0.02)], cx, cy, 32):
        d.ellipse([ex - hw, ey - hw, ex + hw, ey + hw], fill=(255, 255, 255, 255))
    # Bristles: a wide trapezoid with a soft comb of lines
    bx, by = cx, cy + S * 0.02
    head = [(bx - S * 0.075, by), (bx + S * 0.075, by), (bx + S * 0.15, by + S * 0.245), (bx - S * 0.15, by + S * 0.245)]
    d.polygon(rot(head, cx, cy, 32), fill=(255, 255, 255, 255))
    # tie band
    band = [(bx - S * 0.082, by + S * 0.005), (bx + S * 0.082, by + S * 0.005), (bx + S * 0.088, by + S * 0.045), (bx - S * 0.088, by + S * 0.045)]
    d.polygon(rot(band, cx, cy, 32), fill=(255, 200, 70, 255))
    comb = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    cd = ImageDraw.Draw(comb)
    for i in range(-4, 5):
        x0 = bx + i * S * 0.0175
        cd.line(rot([(x0, by + S * 0.07), (x0 * 1.0 + i * S * 0.0075, by + S * 0.245)], cx, cy, 32), fill=(20, 120, 190, 150), width=int(S * 0.006))
    layer = Image.alpha_composite(layer, comb)
    d = ImageDraw.Draw(layer)
    # Sparkles
    for (x, y, r) in [(S * 0.775, S * 0.30, S * 0.085), (S * 0.30, S * 0.30, S * 0.05), (S * 0.71, S * 0.52, S * 0.04)]:
        star(d, x, y, r, (255, 255, 255, 255))
    return layer

def main():
    base = gradient().convert("RGBA")
    mask = squircle_mask()
    # soft top highlight
    hi = Image.new("RGBA", (S, S), (255, 255, 255, 0))
    ImageDraw.Draw(hi).ellipse([-S * 0.2, -S * 0.55, S * 1.2, S * 0.45], fill=(255, 255, 255, 46))
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
