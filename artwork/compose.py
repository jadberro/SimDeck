#!/usr/bin/env python3
"""Composite live pointers onto the extracted face - the exact operation the
app and the firmware perform - to verify pivots and scale angles.

    python compose.py 2.8 1.0 1.0     # accu marker, both brakes at the 1 tick
"""
import json, math, os, sys
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "build")


def angle_for(points, v):
    """Piecewise-linear value -> degrees, clamped to the scale ends."""
    pts = sorted(points)
    if v <= pts[0][0]: return pts[0][1]
    if v >= pts[-1][0]: return pts[-1][1]
    for (v0, a0), (v1, a1) in zip(pts, pts[1:]):
        if v0 <= v <= v1:
            return a0 + (v - v0) / (v1 - v0) * (a1 - a0)
    return pts[-1][1]


def needle(draw, sc, value_k, colour, scale=1.0, style="plain"):
    """style 'plain' is a flat wedge. 'edged' matches the reference more
    closely: a fuller taper, a soft dark outline, and a slightly lighter
    ridge down the centre so it reads as a bevelled metal pointer."""
    px, py = [c * scale for c in sc["pivot"]]
    deg = angle_for(sc["points"], value_k)
    ln, w, boss = sc["needle_len"] * scale, sc["needle_w"] * scale, sc["boss_r"] * scale
    root = boss - 8 * scale
    t = math.radians(deg)

    def place(pts):
        return [(px + x * math.cos(t) - y * math.sin(t),
                 py + x * math.sin(t) + y * math.cos(t)) for x, y in pts]

    if style == "plain":
        body, tip = ln * 0.45, 2.0 * scale
        pts = [(root, -w / 2), (body, -w / 2), (ln, -tip), (ln, tip), (body, w / 2), (root, w / 2)]
        draw.polygon(place(pts), fill=colour)
        return

    # spade: the real pointer keeps its width for most of its length and
    # only tapers over the last third, to a blunt tip about a third as wide.
    # A slightly darker tone than pure white grey, and a one-pixel darker
    # edge along the two long sides so it sits on the face rather than on top.
    shoulder = ln * 0.66
    tip = w * 0.16
    pts = [(root, -w / 2), (shoulder, -w / 2), (ln, -tip), (ln, tip),
           (shoulder, w / 2), (root, w / 2)]
    draw.polygon(place(pts), fill=(88, 91, 94))            # edge
    e = 1.1 * scale
    inner = [(root, -w / 2 + e), (shoulder, -w / 2 + e), (ln - e, -tip + e * 0.4),
             (ln - e, tip - e * 0.4), (shoulder, w / 2 - e), (root, w / 2 - e)]
    draw.polygon(place(inner), fill=colour)


def render(accum, left, right, path, supersample=3, style="plain", accu_len=1.0):
    geom = json.load(open(os.path.join(OUT, "geometry.json")))
    geom["scales"]["accum"]["needle_len"] *= accu_len
    face = Image.open(os.path.join(OUT, geom["face"])).convert("RGB")
    S = geom["size"] * supersample
    big = face.resize((S, S), Image.LANCZOS)
    d = ImageDraw.Draw(big)
    col = geom["needle_colour"]
    needle(d, geom["scales"]["accum"], accum, col, supersample, style)
    needle(d, geom["scales"]["brake_l"], left, col, supersample, style)
    needle(d, geom["scales"]["brake_r"], right, col, supersample, style)
    img = big.resize((geom["size"], geom["size"]), Image.LANCZOS)
    img.save(path)
    return img


if __name__ == "__main__":
    v = [float(x) for x in sys.argv[1:4]] if len(sys.argv) >= 4 else [2.8, 1.0, 1.0]
    render(v[0], v[1], v[2], os.path.join(OUT, "compose.png"))
    print("wrote build/compose.png at", v)
