#!/usr/bin/env python3
"""Draws the DevSweep app icon and writes App/Assets.xcassets/AppIcon.appiconset.

    python3 scripts/make-icon.py

Needs Pillow (pip install pillow). A rounded square in macOS's icon shape with
a simple storage bar, like the one in System Settings: a coloured used segment,
a grey segment and a dark free segment.
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
    w, h = S * 0.64, S * 0.23
    x0, y0 = (S - w) / 2, (S - h) / 2
    r = h / 2
    # whole bar = the free (dark) part, then the used parts on top, clipped to the bar's shape
    bar = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    bd = ImageDraw.Draw(bar)
    bd.rectangle([x0, y0, x0 + w, y0 + h], fill=(86, 86, 106, 255))                       # free
    bd.rectangle([x0, y0, x0 + w * 0.72, y0 + h], fill=(142, 142, 147, 255))              # used
    bd.rectangle([x0, y0, x0 + w * 0.17, y0 + h], fill=(240, 85, 60, 255))                # the part you can act on
    bd.line([(x0 + w * 0.17, y0), (x0 + w * 0.17, y0 + h)], fill=(30, 30, 40, 255), width=int(S * 0.006))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([x0, y0, x0 + w, y0 + h], radius=r, fill=255)
    layer.paste(bar, (0, 0), mask)
    # a soft highlight along the top edge of the bar
    hl = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(hl).rounded_rectangle([x0 + r * 0.4, y0 + h * 0.08, x0 + w - r * 0.4, y0 + h * 0.34], radius=h * 0.13, fill=(255, 255, 255, 40))
    layer = Image.alpha_composite(layer, hl)
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
