#!/usr/bin/env python3
"""
Regenerates DevSweep's Windows icon files from the project logo (docs/icon.png, 1024x1024 RGBA).

    python3 windows/eng/make-icons.py

Writes, under windows/src/DevSweep.App/Assets/:
  DevSweep.ico   16, 24, 32, 48, 64, 128 and 256 px (what the exe, the window, the taskbar,
                 the tray and the installer's shortcuts use)
  DevSweep.png   256 px (the title bar's logo and the toast's logo)

The generated files are committed, so building never needs Python or Pillow.
"""
import pathlib
import sys

from PIL import Image

SIZES = [16, 24, 32, 48, 64, 128, 256]
root = pathlib.Path(__file__).resolve().parents[2]
source = root / "docs" / "icon.png"
out = root / "windows" / "src" / "DevSweep.App" / "Assets"
out.mkdir(parents=True, exist_ok=True)

logo = Image.open(source).convert("RGBA")
if logo.size != (1024, 1024):
    sys.exit(f"{source} is {logo.size}, expected 1024x1024")

frames = {size: logo.resize((size, size), Image.LANCZOS) for size in SIZES}
# Pillow writes one directory entry per size when given the largest image plus sizes=.
frames[256].save(out / "DevSweep.ico", format="ICO", sizes=[(s, s) for s in SIZES], append_images=[frames[s] for s in SIZES if s != 256])
frames[256].save(out / "DevSweep.png", format="PNG", optimize=True)
print("wrote", out / "DevSweep.ico", (out / "DevSweep.ico").stat().st_size, "bytes")
print("wrote", out / "DevSweep.png", (out / "DevSweep.png").stat().st_size, "bytes")
