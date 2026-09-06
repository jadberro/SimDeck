#!/usr/bin/env python3
"""Generate the A320 accumulator/brake dial artwork and the matching
firmware geometry header.

One set of constants drives both the SVG and gauge_geometry.h, so the
painted scale and the needle angles can never drift apart.

Angle convention (shared with firmware):
    degrees, 0 = east (3 o'clock), positive = clockwise, y axis down.
    Straight down is +90, straight up is -90.

Outputs (into ./build):
    dial_face.svg / .png     480x480 dial artwork
    needle_accu.svg / .png   accumulator pointer
    needle_brake.svg / .png  brake pointer
    gauge_geometry.h         constants for the firmware
"""

import math
import os

SIZE = 480
CX = CY = SIZE / 2.0

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "build")

# ---------------------------------------------------------------------------
# Physical basis
#
# ARINC 408A square case sizes, from Avionics Mounts Inc. clamp drawings
# (https://avionicsmounts.com/pdf/ARINC%20408A%20Square%20Clamps.pdf):
#
#   size     clamp A   clamp B   clamp C    panel cutout (nominal)
#   2ATI-S   2.175"    2.080"    2.500"     2.200"  (55.9 mm)
#   3ATI-S   3.175"    3.060"    3.885"     3.200"  (81.3 mm)
#
# NOT VERIFIED: which size the A320 triple indicator is. No datasheet or
# parts listing with dimensions could be found, and scaling off the
# reference photo is unreliable because the hand is nearer the camera than
# the gauge. 2ATI is the working assumption - measure your panel and change
# CASE if it is wrong. Everything below is derived, so one edit re-scales
# the artwork and the firmware header together.
# ---------------------------------------------------------------------------

ARINC_408A = {
    "2ATI": dict(cutout_mm=55.9, clamp_a_mm=55.2, clamp_b_mm=52.8,
                 clamp_c_mm=63.5, dial_mm=45.0),
    "3ATI": dict(cutout_mm=81.3, clamp_a_mm=80.6, clamp_b_mm=77.7,
                 clamp_c_mm=98.7, dial_mm=68.0),
}

CASE = "2ATI"
DIAL_MM = ARINC_408A[CASE]["dial_mm"]   # visible dial face diameter
PX_PER_MM = (SIZE * 0.98) / DIAL_MM     # render scale, ~2% margin


def mm(v):
    """Millimetres -> render pixels."""
    return v * PX_PER_MM


# ---------------------------------------------------------------------------
# Scale definitions. Tweak these against a straight-on photo of the real
# instrument; everything downstream follows automatically.
# ---------------------------------------------------------------------------

# Positions were measured off the reference on a radius-fraction grid into a
# 168px face; K rescales them into the full-screen face below. Edit the
# numbers in 168px terms and leave K alone.
FACE_R = 236.0
K = FACE_R / 168.0


def sc_(**d):
    """Scale a 168px-face definition to the real face radius."""
    for key in ("r_out", "r_in", "green_w", "r_label", "needle_len", "needle_w"):
        d[key] *= K
    d["cx"] = CX + (d["cx"] - CX) * K
    d["cy"] = CY + (d["cy"] - CY) * K
    return d


ACCUM = sc_(
    name="accum",
    cx=CX, cy=123.0,             # pivot high; the boss hides the needle root
    r_out=107.0, r_in=98.0,      # ticks point outward (down) from a thin arc
    green_w=8.0,
    r_label=113.0,
    vmin=0.0, vmax=4.0,
    a_min=131.0, a_max=49.0,     # only ~82 degrees: a flat, shallow arc
    major=1.0, minor=0.5,
    ticks_from=0.0, ticks_to=4.0,
    labels=[(0.0, "0"), (4.0, "4")],
    green=(2.85, 3.15),          # a short marker at 3000 psi, not a band
    needle_len=96.0, needle_w=22.0,
)

BRAKE_L = sc_(
    name="brake_l",
    cx=141.0, cy=312.0,
    r_out=88.0, r_in=75.0,
    green_w=11.0,
    r_label=94.0,                # just beyond the tick tips
    vmin=0.0, vmax=3.0,
    a_min=10.0, a_max=-118.0,    # 0 just below horizontal, 3 past vertical
    major=1.0, minor=0.5,
    ticks_from=1.0, ticks_to=3.0,
    labels=[(1.0, "1"), (3.0, "3")],
    green=(0.0, 0.94),
    needle_len=76.0, needle_w=24.0,   # stubby: stops well short of the scale
)

BRAKE_R = sc_(
    name="brake_r",
    cx=339.0, cy=312.0,
    r_out=88.0, r_in=75.0,
    green_w=11.0,
    r_label=94.0,
    vmin=0.0, vmax=3.0,
    a_min=170.0, a_max=298.0,
    major=1.0, minor=0.5,
    ticks_from=1.0, ticks_to=3.0,
    labels=[(1.0, "1"), (3.0, "3")],
    green=(0.0, 0.94),
    needle_len=76.0, needle_w=24.0,
)

SCALES = [ACCUM, BRAKE_L, BRAKE_R]

# Palette taken from the in-sim rendering of the real instrument. The green
# is a teal, not a grass green; the units caption is blue, not white; and the
# markings are a soft grey rather than pure white, which is what stops it
# looking like a diagram.
FONT = "Routed Gothic, Routed Gothic Wide, Helvetica, Arial, sans-serif"

NUMERAL = 46          # numeral font size, in full-face pixels

VARIANTS = {
    # closest to the in-sim rendering: dark grey face so the bosses read as
    # holes, moderate lettering weight
    "A": dict(face="#141618", boss="#050607", boss_rim=None,
              stroke=0.075, arc=2.6, tick_major=3.4, tick_minor=2.2,
              text=1.00, needle="#c2c6ca"),
    # heavier everything: for a small physical screen viewed from a seat
    "B": dict(face="#111315", boss="#050607", boss_rim=None,
              stroke=0.110, arc=3.4, tick_major=4.2, tick_minor=2.8,
              text=1.08, needle="#c8ccd0"),
    # true black face; the bosses need a faint rim or they vanish
    "C": dict(face="#000000", boss="#0b0d0f", boss_rim="#20242a",
              stroke=0.085, arc=2.8, tick_major=3.6, tick_minor=2.4,
              text=1.00, needle="#c2c6ca"),
}
V = VARIANTS["A"]

def select_variant(name):
    """Switch the active variant. Must be called before build_face()."""
    global V, FACE, BOSS_FILL2, NEEDLE, STROKE_RATIO
    V = VARIANTS[name]
    FACE = V["face"]
    BOSS_FILL2 = V["boss"]
    NEEDLE = V["needle"]
    STROKE_RATIO = V["stroke"]

STROKE_RATIO = V["stroke"]

WHITE = "#c9cdd1"
GREEN = "#12a395"
BLUE = "#4c86c6"
FACE = V["face"]

PANEL = "#262e38"        # surround the instrument is set into
BEZEL_MID = "#333d49"    # octagonal bezel plate
BEZEL_HI = "#4e5b6a"
BEZEL_LO = "#232b35"
BEZEL_RING = "#0c0e11"   # dark gap between bezel and glass
NEEDLE = V["needle"]
BOSS_FILL2 = V["boss"]
BOSS_RIM2 = "#1e242b"

# ---------------------------------------------------------------------------
# Hardware appearance
#
# The panel is an LCD pretending to be a sealed instrument. What sells it is
# fixed physical structure - bezel ring, glass edge, recessed pivot bosses -
# not painted-on glare. Real glare moves with your head; baked glare stays
# put and immediately reads as a picture of a gauge. So: geometry yes,
# reflections no.
# ---------------------------------------------------------------------------

BEZEL_OUTER = 239.0     # edge of the visible LCD circle
BEZEL_INNER = 214.0     # where the black dial face starts
BEZEL_FACE = "#5f6d76"  # instrument case front, blue-grey
BEZEL_HI = "#7d8b94"    # bevel catching light from above
BEZEL_LO = "#3b464d"    # bevel in shade
GLASS_EDGE = "#14181b"  # dark gap between bezel and dial under the glass

BOSS_FILL = "#141414"   # recessed well each pointer rises out of
BOSS_RIM = "#2a2c2e"
BOSS_R = {k: v * K for k, v in {"accum": 31.0, "brake_l": 33.0, "brake_r": 33.0}.items()}

SHADOW = "#000000"
SHADOW_ALPHA = 0.45
SHADOW_DX, SHADOW_DY = 4.0, 5.0   # light from upper left, fixed

# ---------------------------------------------------------------------------


def pt(cx, cy, r, deg):
    a = math.radians(deg)
    return (cx + r * math.cos(a), cy + r * math.sin(a))


def angle_for(sc, value):
    t = (value - sc["vmin"]) / (sc["vmax"] - sc["vmin"])
    return sc["a_min"] + t * (sc["a_max"] - sc["a_min"])


def arc_path(cx, cy, r, a0, a1, width, colour):
    """Stroked arc. Handles either sweep direction."""
    x0, y0 = pt(cx, cy, r, a0)
    x1, y1 = pt(cx, cy, r, a1)
    delta = a1 - a0
    large = 1 if abs(delta) > 180 else 0
    sweep = 1 if delta > 0 else 0
    return ('<path d="M %.2f %.2f A %.2f %.2f 0 %d %d %.2f %.2f" '
            'fill="none" stroke="%s" stroke-width="%.1f" '
            'stroke-linecap="butt"/>'
            % (x0, y0, r, r, large, sweep, x1, y1, colour, width))


def frange(lo, hi, step):
    n = int(round((hi - lo) / step))
    return [lo + i * step for i in range(n + 1)]


def build_scale(sc):
    out = []
    cx, cy = sc["cx"], sc["cy"]

    g0, g1 = sc["green"]

    # The green band IS the scale arc over its range, not a separate inner
    # arc: white ticked arc and green run at the same radius and meet.
    out.append(arc_path(cx, cy, sc["r_in"] - sc["green_w"] / 2.0 + 1.5,
                        angle_for(sc, g0), angle_for(sc, g1),
                        sc["green_w"], GREEN))

    # white baseline over the ungreened part only
    out.append(arc_path(cx, cy, sc["r_in"],
                        angle_for(sc, g1), angle_for(sc, sc["vmax"]),
                        V["arc"], WHITE))
    if g0 > sc["vmin"] + 1e-6:
        out.append(arc_path(cx, cy, sc["r_in"],
                            angle_for(sc, sc["vmin"]), angle_for(sc, g0),
                            2.0, WHITE))

    # ticks - none over the green, which carries no graduations
    for v in frange(sc["vmin"], sc["vmax"], sc["minor"]):
        if sc["ticks_from"] - 1e-6 <= v <= sc["ticks_to"] + 1e-6:
            pass
        else:
            continue
        major = abs((v / sc["major"]) - round(v / sc["major"])) < 1e-6
        a = angle_for(sc, v)
        r0 = sc["r_in"]
        r1 = sc["r_out"] if major else sc["r_in"] + (sc["r_out"] - sc["r_in"]) * 0.6
        x0, y0 = pt(cx, cy, r0, a)
        x1, y1 = pt(cx, cy, r1, a)
        out.append('<line x1="%.2f" y1="%.2f" x2="%.2f" y2="%.2f" '
                   'stroke="%s" stroke-width="%.1f" stroke-linecap="butt"/>'
                   % (x0, y0, x1, y1, WHITE,
                      V["tick_major"] if major else V["tick_minor"]))

    # numerals
    for v, text in sc["labels"]:
        a = angle_for(sc, v)
        x, y = pt(cx, cy, sc["r_label"], a)
        out.append('<text x="%.2f" y="%.2f" fill="%s" stroke="%s" '
                   'stroke-width="%.2f" stroke-linejoin="round" '
                   'font-size="%d" font-family="%s" font-weight="400" '
                   'text-anchor="middle" dominant-baseline="central">%s</text>'
                   % (x, y, WHITE, WHITE, NUMERAL * STROKE_RATIO,
                      int(round(NUMERAL * V["text"])), FONT, text))

    # The pointer rises out of a deep well, not off a painted dot. On the real
    # instrument this boss is large - roughly a third of the scale radius.
    # Big and flat black. A visible rim reads as a printed ring rather than
    # as a hole the pointer comes out of.
    r = BOSS_R[sc["name"]]
    rim = ('stroke="%s" stroke-width="2"' % V["boss_rim"]) if V["boss_rim"] else ""
    out.append('<circle cx="%.2f" cy="%.2f" r="%.1f" fill="%s" %s/>'
               % (cx, cy, r, BOSS_FILL2, rim))
    return out


def build_bezel():
    """Screen-only: the whole circle is dial face. Nothing else exists.

    The panel and bezel that surround the real instrument are physical; a
    printed housing will provide them. Drawing them on the screen just wasted
    a third of the pixels.
    """
    return ['<rect width="%d" height="%d" fill="#000000"/>' % (SIZE, SIZE),
            '<circle cx="%.1f" cy="%.1f" r="%.1f" fill="%s"/>'
            % (CX, CY, FACE_R, FACE)]


def build_face():
    p = ['<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" '
         'viewBox="0 0 %d %d">' % (SIZE, SIZE, SIZE, SIZE)]
    p.extend(build_bezel())

    for sc in SCALES:
        p.extend(build_scale(sc))

    def label(x, y, text, size, weight="500", spacing="1"):
        return ('<text x="%.1f" y="%.1f" fill="%s" stroke="%s" '
                'stroke-width="%.2f" stroke-linejoin="round" '
                'font-size="%d" font-family="%s" font-weight="400" '
                'letter-spacing="%s" text-anchor="middle" '
                'dominant-baseline="central">%s</text>'
                % (x, y, WHITE, WHITE, size * STROKE_RATIO, size, FONT,
                   spacing, text))

    def L(x, y, text, size, weight="400", spacing="1"):
        return label(CX + (x - CX) * K, CY + (y - CY) * K, text,
                     int(round(size * K * V["text"])), weight, spacing)

    p.append(L(156, 145, "ACCU", 27))
    p.append(L(313, 145, "PRESS", 27))
    p.append(L(CX, 350, "0", 28))
    p.append(L(CX, 381, "BRAKES", 24, "500", "1"))
    p.append(L(CX, 402, "PSIx1000", 17).replace(WHITE, BLUE))

    p.append("</svg>")
    return "\n".join(p)


def needle_art_size(length, boss_r=0.0, width=13.0):
    """(w, h, pivot_x, pivot_y) for a needle bitmap.

    The pivot sits at the bottom of the bitmap. The body starts at the boss
    radius rather than at (or behind) the pivot, so nothing pokes out from
    under the boss on the far side - the wedge appears to emerge from it,
    which is what the real pointer does.
    """
    w = int(width + 6)
    h = int(length + 3)
    return w, h, w / 2.0, length + 1.0


def build_needle(length, boss_r, width=13.0, tip=4.0, shadow=False):
    """Needle art points straight up, pivot at (w/2, length+1)."""
    w, h, px, py = needle_art_size(length, boss_r, width)
    root = py - (boss_r - 3.0)          # y where the body leaves the boss
    body = py - length * 0.40           # widest just clear of the boss
    pts = [(px - width / 2, root),
           (px - width / 2, body),
           (px - tip / 2, 1.0),
           (px + tip / 2, 1.0),
           (px + width / 2, body),
           (px + width / 2, root)]
    poly = " ".join("%.2f,%.2f" % q for q in pts)

    if shadow:
        out = ""
        for grow, op in ((2.2, 0.18), (1.1, 0.24), (0.0, 0.34)):
            gp = " ".join("%.2f,%.2f" % (x + (grow if x > px else -grow), y)
                          for x, y in pts)
            out += '<polygon points="%s" fill="%s" opacity="%.2f"/>' % (gp, SHADOW, op)
        return ('<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" '
                'viewBox="0 0 %d %d">%s</svg>' % (w, h, w, h, out))

    return ('<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" '
            'viewBox="0 0 %d %d"><polygon points="%s" fill="%s"/></svg>'
            % (w, h, w, h, poly, NEEDLE))


def build_header():
    lines = [
        "// gauge_geometry.h - GENERATED by art/make_dial.py, do not edit.",
        "//",
        "// Angles: degrees, 0 = east, positive = clockwise, y down.",
        "#pragma once",
        "",
        "#define DIAL_SIZE %d" % SIZE,
        "",
        "// fixed light direction: shadow offset in screen pixels",
        "#define SHADOW_DX %d" % int(SHADOW_DX),
        "#define SHADOW_DY %d" % int(SHADOW_DY),
        "",
    ]
    for sc, sym in ((ACCUM, "NEEDLE_ACCU"), (BRAKE_L, "NEEDLE_BRAKE")):
        w, h, px, py = needle_art_size(sc["needle_len"], BOSS_R[sc["name"]], sc["needle_w"])
        lines += [
            "// %s bitmap: pivot position inside the art" % sym.lower(),
            "#define %s_ART_W       %d" % (sym, w),
            "#define %s_ART_H       %d" % (sym, h),
            "#define %s_ART_PIVOT_X %d" % (sym, int(px)),
            "#define %s_ART_PIVOT_Y %d" % (sym, int(py)),
            "",
        ]
    for sc in SCALES:
        n = sc["name"].upper()
        lines += [
            "// %s" % sc["name"],
            "#define %s_PIVOT_X   %.1ff" % (n, sc["cx"]),
            "#define %s_PIVOT_Y   %.1ff" % (n, sc["cy"]),
            "#define %s_VMIN      %.1ff" % (n, sc["vmin"]),
            "#define %s_VMAX      %.1ff" % (n, sc["vmax"]),
            "#define %s_AMIN      %.1ff" % (n, sc["a_min"]),
            "#define %s_AMAX      %.1ff" % (n, sc["a_max"]),
            "#define %s_NEEDLE_LEN %.1ff" % (n, sc["needle_len"]),
            "",
        ]
    return "\n".join(lines)


def main():
    os.makedirs(OUT, exist_ok=True)

    files = {
        "dial_face.svg": build_face(),
        "needle_accu.svg": build_needle(ACCUM["needle_len"], BOSS_R["accum"], ACCUM["needle_w"]),
        "needle_brake.svg": build_needle(BRAKE_L["needle_len"], BOSS_R["brake_l"], BRAKE_L["needle_w"]),
        "needle_accu_shadow.svg": build_needle(ACCUM["needle_len"], BOSS_R["accum"], ACCUM["needle_w"], shadow=True),
        "needle_brake_shadow.svg": build_needle(BRAKE_L["needle_len"], BOSS_R["brake_l"], BRAKE_L["needle_w"], shadow=True),
        "gauge_geometry.h": build_header(),
    }
    for name, text in files.items():
        with open(os.path.join(OUT, name), "w", encoding="utf-8") as fh:
            fh.write(text)
        print("wrote", name)

    try:
        import cairosvg
    except ImportError:
        print("cairosvg not installed - PNGs skipped "
              "(pip install cairosvg)")
        return
    for name in ("dial_face", "needle_accu", "needle_brake",
                 "needle_accu_shadow", "needle_brake_shadow"):
        cairosvg.svg2png(url=os.path.join(OUT, name + ".svg"),
                         write_to=os.path.join(OUT, name + ".png"))
        print("wrote", name + ".png")


if __name__ == "__main__":
    main()
