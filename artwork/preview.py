#!/usr/bin/env python3
"""Render the dial with needles at chosen values.

    python preview.py 3.0 0.0 2.4

Lets you check the angle constants without flashing anything. If the
preview looks right, the firmware will look right - both read the same
numbers out of make_dial.py.
"""

import os
import sys

import make_dial as md


def needle_svg(sc, value):
    a = md.angle_for(sc, value)
    cx, cy = sc["cx"], sc["cy"]
    ln = sc["needle_len"]
    w = sc["needle_w"]
    root = md.BOSS_R[sc["name"]] - 8.0     # start well under the boss edge
    body = ln * 0.40
    tip = 4.0

    pts = [(root, -w / 2), (body, -w / 2), (ln, -tip / 2),
           (ln, tip / 2), (body, w / 2), (root, w / 2)]
    poly = " ".join("%.2f,%.2f" % p for p in pts)
    return ('<g transform="translate(%.2f,%.2f) rotate(%.3f)">'
            '<polygon points="%s" fill="%s"/></g>' % (cx, cy, a, poly, md.NEEDLE))


def render(accum, left, right, path):
    face = md.build_face()
    body = face[:face.rindex("</svg>")]
    body += needle_svg(md.ACCUM, accum)
    body += needle_svg(md.BRAKE_L, left)
    body += needle_svg(md.BRAKE_R, right)
    body += "</svg>"

    with open(path + ".svg", "w", encoding="utf-8") as fh:
        fh.write(body)
    try:
        import cairosvg
        cairosvg.svg2png(bytestring=body.encode(), write_to=path + ".png")
    except ImportError:
        pass
    return body


if __name__ == "__main__":
    vals = [float(x) for x in sys.argv[1:4]] if len(sys.argv) >= 4 else [3.0, 0.0, 2.4]
    os.makedirs(md.OUT, exist_ok=True)
    out = os.path.join(md.OUT, "preview")
    render(vals[0], vals[1], vals[2], out)
    print("accum=%.2f left=%.2f right=%.2f -> %s.png" % (vals[0], vals[1], vals[2], out))
