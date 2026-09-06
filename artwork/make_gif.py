#!/usr/bin/env python3
"""Render an animated GIF of the mock brake cycle.

Uses the same decoded LVGL byte arrays the firmware will display and the
same smoothing constants the sketch uses, so this is a faithful preview of
panel behaviour rather than a mock-up of it.

    python make_gif.py
"""

import math
import os

from PIL import Image

import make_dial as md
import verify_render as vr

CYCLE = 8.0        # compressed from the mock's 20s so the loop is watchable
FPS = 12
OUT_SIZE = 300

# smoothing per 30Hz tick, matching accu_panel.ino
SMOOTH = {"accum": 0.12, "left": 0.16, "right": 0.16}


def mock(t):
    """Mirror of sources/mock.py, retimed to CYCLE."""
    phase = (t % CYCLE) / CYCLE
    accum = 2950.0 + 60.0 * math.sin(t * 0.8)
    target = 2700.0 if phase < 0.40 else 0.0
    ramp = min(1.0, (phase % 0.40) / 0.05)
    left = max(0.0, target * ramp + 40.0 * math.sin(t * 2.1))
    right = max(0.0, target * ramp + 40.0 * math.sin(t * 2.1 + 0.4))
    return accum, left, right


def main():
    b = md.OUT
    face = vr.load_c_array(os.path.join(b, "img_dial_face.c"),
                           md.SIZE, md.SIZE, alpha=False).convert("RGBA")
    aw, ah, apx, apy = md.needle_art_size(md.ACCUM["needle_len"])
    bw, bh, bpx, bpy = md.needle_art_size(md.BRAKE_L["needle_len"])
    accu = vr.load_c_array(os.path.join(b, "img_needle_accu.c"), aw, ah)
    brake = vr.load_c_array(os.path.join(b, "img_needle_brake.c"), bw, bh)
    accu_s = vr.load_c_array(os.path.join(b, "img_needle_accu_shadow.c"), aw, ah)
    brake_s = vr.load_c_array(os.path.join(b, "img_needle_brake_shadow.c"), bw, bh)

    shown = {"accum": 0.0, "left": 0.0, "right": 0.0}
    frames = []
    n = int(CYCLE * FPS)
    tick = 1.0 / 30.0
    carry = 0.0

    for i in range(n):
        t = i / float(FPS)
        raw = dict(zip(("accum", "left", "right"), mock(t)))

        # advance the firmware's 30Hz smoothing across this frame's worth of time
        carry += 1.0 / FPS
        while carry >= tick:
            carry -= tick
            for k in shown:
                shown[k] += (raw[k] - shown[k]) * SMOOTH[k]

        img = face.copy()
        jobs = (("accum", md.ACCUM, accu, accu_s, apx, apy),
                ("left", md.BRAKE_L, brake, brake_s, bpx, bpy),
                ("right", md.BRAKE_R, brake, brake_s, bpx, bpy))
        for key, sc, art, shd, ax, ay in jobs:
            deg = md.angle_for(sc, shown[key] / 1000.0)
            vr.place(img, shd, int(ax), int(ay),
                     sc["cx"] + md.SHADOW_DX, sc["cy"] + md.SHADOW_DY, deg)
        for key, sc, art, shd, ax, ay in jobs:
            deg = md.angle_for(sc, shown[key] / 1000.0)
            vr.place(img, art, int(ax), int(ay), sc["cx"], sc["cy"], deg)

        frames.append(img.convert("RGB")
                      .resize((OUT_SIZE, OUT_SIZE), Image.LANCZOS)
                      .quantize(colors=64, method=Image.MEDIANCUT))

    path = os.path.join(b, "accu_panel_demo.gif")
    frames[0].save(path, save_all=True, append_images=frames[1:],
                   duration=int(1000 / FPS), loop=0, optimize=True)
    print("%d frames, %.1f KB -> %s"
          % (len(frames), os.path.getsize(path) / 1024.0, path))


if __name__ == "__main__":
    main()
