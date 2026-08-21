#!/usr/bin/env python3
"""
Erzeugt assets/easyservice.ico aus Code, damit das Icon reproduzierbar ist.

    python tools/generate-icon.py

Gezeichnet wird ein Zahnrad (Dienst) mit gruenem Statuspunkt (laeuft) auf einem
abgerundeten blauen Feld. Jede Groesse wird einzeln gerendert: bei 16 und 20 Pixel
bekommt das Zahnrad weniger, dafuer kraeftigere Zaehne, sonst verschwimmt es.

Benoetigt Pillow:  pip install pillow
"""

from __future__ import annotations

import math
import struct
from pathlib import Path

from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 48, 64, 128, 256]
SUPERSAMPLE = 8

BLUE_TOP = (37, 118, 224)
BLUE_BOTTOM = (14, 63, 148)
GREEN = (52, 199, 89)
WHITE = (255, 255, 255)


def rounded_mask(size: int, radius: float) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return mask


def vertical_gradient(size: int, top: tuple, bottom: tuple) -> Image.Image:
    grad = Image.new("RGB", (1, size))
    for y in range(size):
        t = y / max(1, size - 1)
        grad.putpixel((0, y), tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)))
    return grad.resize((size, size), Image.Resampling.NEAREST)


def gear_polygon(cx: float, cy: float, r_outer: float, r_inner: float, teeth: int) -> list:
    """Zahnrad als Polygon: pro Zahn ein Trapez, dazwischen der Grundkreis."""
    points = []
    steps = 1440
    tooth_span = math.tau / teeth
    # Anteil des Zahns an einer Teilung; der Rest ist Grundkreis, die Flanken sind Rampen.
    flat = tooth_span * 0.30
    ramp = tooth_span * 0.09

    for i in range(steps):
        a = math.tau * i / steps
        phase = (a % tooth_span) - tooth_span / 2

        d = abs(phase)
        if d <= flat / 2:
            r = r_outer
        elif d >= flat / 2 + ramp:
            r = r_inner
        else:
            t = (d - flat / 2) / ramp
            r = r_outer + (r_inner - r_outer) * t

        points.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    return points


def render(size: int) -> Image.Image:
    s = size * SUPERSAMPLE
    tiny = size <= 20

    # Hintergrund: abgerundetes Feld mit Farbverlauf
    bg = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    gradient = vertical_gradient(s, BLUE_TOP, BLUE_BOTTOM).convert("RGBA")
    bg.paste(gradient, (0, 0), rounded_mask(s, s * 0.22))

    # Zahnrad als Maske, damit die Nabe den Hintergrund durchscheinen laesst
    cx, cy = s * 0.445, s * 0.435
    r_outer = s * 0.335 if tiny else s * 0.320
    r_inner = r_outer * (0.74 if tiny else 0.70)
    teeth = 6 if tiny else 8
    hub = r_outer * (0.34 if tiny else 0.32)

    gear = Image.new("L", (s, s), 0)
    gd = ImageDraw.Draw(gear)
    gd.polygon(gear_polygon(cx, cy, r_outer, r_inner, teeth), fill=255)
    gd.ellipse([cx - hub, cy - hub, cx + hub, cy + hub], fill=0)

    bg.paste(Image.new("RGBA", (s, s), WHITE + (255,)), (0, 0), gear)

    # Statuspunkt unten rechts, mit blauem Ring abgesetzt
    dot_r = s * (0.185 if tiny else 0.152)
    dx, dy = s * 0.775, s * 0.783
    ring = dot_r * 1.42
    d = ImageDraw.Draw(bg)
    d.ellipse([dx - ring, dy - ring, dx + ring, dy + ring], fill=BLUE_BOTTOM + (255,))
    d.ellipse([dx - dot_r, dy - dot_r, dx + dot_r, dy + dot_r], fill=GREEN + (255,))

    return bg.resize((size, size), Image.Resampling.LANCZOS)


def bmp_entry(img: Image.Image) -> bytes:
    """Ein ICO-Eintrag im BMP-Format (32 bpp) inklusive leerer AND-Maske."""
    w, h = img.size
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)

    pixels = img.load()
    rows = []
    for y in range(h - 1, -1, -1):          # BMP-Zeilen laufen von unten nach oben
        row = bytearray()
        for x in range(w):
            r, g, b, a = pixels[x, y]
            row += bytes((b, g, r, a))
        rows.append(bytes(row))

    mask_stride = ((w + 31) // 32) * 4      # 1 bpp, auf 4 Byte aufgerundet
    and_mask = b"\x00" * (mask_stride * h)
    return header + b"".join(rows) + and_mask


def build_ico(images: list, path: Path) -> None:
    entries, blobs = [], []
    offset = 6 + 16 * len(images)

    for img in images:
        w, h = img.size
        blob = img_to_png(img) if w >= 64 else bmp_entry(img)
        entries.append(struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(blob), offset))
        blobs.append(blob)
        offset += len(blob)

    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(images)))
        for e in entries:
            f.write(e)
        for b in blobs:
            f.write(b)


def img_to_png(img: Image.Image) -> bytes:
    import io

    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def main() -> None:
    root = Path(__file__).resolve().parent.parent
    images = [render(s) for s in SIZES]

    ico = root / "assets" / "easyservice.ico"
    build_ico(images, ico)
    print(f"{ico.relative_to(root)}  ({ico.stat().st_size} Bytes, Groessen: {', '.join(map(str, SIZES))})")

    # Vorschau fuer README und Store-Eintraege
    preview = root / "assets" / "easyservice-256.png"
    images[-1].save(preview)
    print(f"{preview.relative_to(root)}")


if __name__ == "__main__":
    main()
