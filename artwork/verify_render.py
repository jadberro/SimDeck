#!/usr/bin/env python3
"""Render the generated LVGL C arrays exactly as the firmware will.

This decodes the real RGB565 byte arrays and composites the needles using
the same pivots the sketch uses. If this looks right, the panel looks
right - it catches encoding mistakes and pivot errors before you spend an
evening wondering why every needle over-reads.

    python verify_render.py 3.0 0.0 2.4
"""

import math
import os
import re
import sys

from PIL import Image

import make_dial as md

PAD = 120


def load_c_array(path, w, h, alpha=True):
    src = open(path, encoding="utf-8").read()
    body = src[src.index("_map[] = {") + 10: src.index("};")]
    data = bytes(int(x, 16) for x in re.findall(r"0x([0-9a-f]{2})", body))
    step = 3 if alpha else 2
    if len(data) != w * h * step:
        raise SystemExit("%s: expected %d bytes, got %d"
                         % (path, w * h * step, len(data)))
    im = Image.new("RGBA", (w, h))
    px = im.load()
    for i in range(w * h):
        lo, hi = data[i * step], data[i * step + 1]
        a = data[i * step + 2] if alpha else 255
        c = lo | (hi << 8)
        px[i % w, i // w] = (((c >> 11) & 0x1F) * 255 // 31,
                             ((c >> 5) & 0x3F) * 255 // 63,
                             (c & 0x1F) * 255 // 31, a)
    return im


def place(canvas, art, apx, apy, px, py, deg):
    """Pad so the pivot is at the centre of a big square before rotating.

    Rotating in place would clip the needle against its own bitmap bounds.
    LVGL extends the draw area for rotated images, so padding here is what
    actually matches the firmware.
    """
    big = Image.new("RGBA", (PAD * 2, PAD * 2), (0, 0, 0, 0))
    big.alpha_composite(art, (PAD - apx, PAD - apy))
    rot = big.rotate(-(deg + 90), resample=Image.BICUBIC, center=(PAD, PAD))
    canvas.alpha_composite(rot, (int(px) - PAD, int(py) - PAD))


def main():
    vals = ([float(x) for x in sys.argv[1:4]]
            if len(sys.argv) >= 4 else [3.0, 0.0, 2.4])

    b = md.OUT
    face = load_c_array(os.path.join(b, "img_dial_face.c"),
                        md.SIZE, md.SIZE, alpha=False).convert("RGBA")

    aw, ah, apx, apy = md.needle_art_size(md.ACCUM["needle_len"])
    bw, bh, bpx, bpy = md.needle_art_size(md.BRAKE_L["needle_len"])
    accu = load_c_array(os.path.join(b, "img_needle_accu.c"), aw, ah)
    brake = load_c_array(os.path.join(b, "img_needle_brake.c"), bw, bh)
    accu_s = load_c_array(os.path.join(b, "img_needle_accu_shadow.c"), aw, ah)
    brake_s = load_c_array(os.path.join(b, "img_needle_brake_shadow.c"), bw, bh)

    jobs = [(vals[0], md.ACCUM, accu, accu_s, apx, apy),
            (vals[1], md.BRAKE_L, brake, brake_s, bpx, bpy),
            (vals[2], md.BRAKE_R, brake, brake_s, bpx, bpy)]

    # all shadows first, so one pointer's shadow never lands on top of
    # another pointer
    for v, sc, art, shd, ax, ay in jobs:
        place(face, shd, int(ax), int(ay),
              sc["cx"] + md.SHADOW_DX, sc["cy"] + md.SHADOW_DY,
              md.angle_for(sc, v))

    for v, sc, art, shd, ax, ay in jobs:
        deg = md.angle_for(sc, v)
        place(face, art, int(ax), int(ay), sc["cx"], sc["cy"], deg)
        rad = math.radians(deg)
        print("%-8s %.2f -> %6.1f deg, tip (%.0f,%.0f)"
              % (sc["name"], v, deg,
                 sc["cx"] + sc["needle_len"] * math.cos(rad),
                 sc["cy"] + sc["needle_len"] * math.sin(rad)))

    out = os.path.join(b, "firmware_sim.png")
    face.convert("RGB").save(out)
    print("wrote", out)


if __name__ == "__main__":
    main()
