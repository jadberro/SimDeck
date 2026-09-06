#!/usr/bin/env python3
"""Build the dial face from the reference image instead of redrawing it.

Faithful by construction: texture, lettering, weight and colour are the real
thing. Three operations only:

  1. find the dial circle and mask everything outside it to black
  2. lift the three painted needles out, filling with the local face colour
  3. measure the pivots and scale angles, so live needles land correctly

Writes build/dial_face.png and updates the pivot/angle constants used by the
app and the firmware.
"""

import math
import os
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "build")
SIZE = 480

REF = os.path.join(HERE, "source", "reference_photo.png")


def find_circle(a):
    """The face is dark and the bezel around it is light. For candidate
    centres near the visual middle, find the radius with the sharpest
    brightness jump; the true centre gives the cleanest single edge."""
    H, W, _ = a.shape
    bright = a.mean(axis=2)
    yy, xx = np.mgrid[0:H, 0:W]
    best = None
    for cx in range(W // 2 - 20, W // 2 + 24, 4):
        for cy in range(H // 2 - 20, H // 2 + 24, 4):
            d = np.hypot(xx - cx, yy - cy)
            prof = np.array([bright[(d >= r) & (d < r + 2)].mean()
                             for r in range(int(W * 0.30), int(W * 0.42), 2)])
            j = np.argmax(np.diff(prof))
            if best is None or np.diff(prof)[j] > best[0]:
                best = (np.diff(prof)[j], cx, cy, int(W * 0.30) + 2 * j)
    return float(best[1]), float(best[2]), float(best[3])


def lift_needles(a, cx, cy, R, bosses):
    """Erase the painted pointers.

    Each is a compact cluster of light grey pixels in a narrow angular sector
    off its boss. Mask that sector, then fill it by normalised convolution of
    the surrounding face so the fill inherits the local tone.
    """
    H, W, _ = a.shape
    r, g, b = a[..., 0], a[..., 1], a[..., 2]
    bright = (r + g + b) / 3.0
    grey = (np.abs(r - b) < 30) & (np.abs(r - g) < 30)
    light = grey & (bright > 95)
    yy, xx = np.mgrid[0:H, 0:W]

    mask = np.zeros((H, W), bool)
    angles = {}
    teal = (g > 110) & (r < 90) & (b > 100)

    def dilate(m, n):
        out = m.copy()
        for _ in range(n):
            o = out.copy()
            o[1:, :] |= out[:-1, :]; o[:-1, :] |= out[1:, :]
            o[:, 1:] |= out[:, :-1]; o[:, :-1] |= out[:, 1:]
            out = o
        return out

    # Reach of each pointer from its boss, in dial radii. Kept short of the
    # scale so ticks and numerals are never touched.
    reach = {"accum": 0.61, "brake_l": 0.462, "brake_r": 0.462}

    for name, (bx, by, br) in bosses.items():
        d = np.hypot(xx - bx, yy - by)
        ang = np.degrees(np.arctan2(yy - by, xx - bx))

        # the pointer is the dense angular cluster of light pixels off the boss
        cand = light & (d > br * 0.45) & (d < reach[name] * R) & ~teal
        ys, xs = np.nonzero(cand)
        a_all = np.degrees(np.arctan2(ys - by, xs - bx))
        hist, edges = np.histogram(a_all, bins=72, range=(-180, 180))
        peak = edges[np.argmax(hist)] + 2.5
        angles[name] = peak

        # take the actual light pixels in a wide sector around it, then grow
        # the selection to swallow the darker flanks and the drop shadow
        dang = np.abs(((ang - peak + 180) % 360) - 180)
        core = cand & (dang < 35)
        grown = dilate(core, max(6, int(R / 38))) & (d > br * 0.4) & (d < reach[name] * R) & ~teal

        # The pointer casts a soft shadow that is darker than the face, not
        # lighter, so a brightness test misses it. Take the whole narrow
        # sector along the pointer regardless of tone; the fill sources from
        # either side and the window behind it is uniform enough to hide the seam.
        band = (dang < 20) & (d > br * 0.9) & (d < reach[name] * R) & ~teal
        mask |= grown | band

    # Fill by copying real pixels, not by averaging.
    #
    # The pointers sit inside the dark textured windows behind the bosses. A
    # blurred fill produced a smooth patch of the wrong tone with no grain,
    # which read as a smudge. Copying from elsewhere on the same surface
    # keeps both tone and texture:
    #   - the accumulator region is mirrored from the clean opposite side of
    #     the face (the dial is left/right symmetric)
    #   - each brake region is copied sideways from the same window, shifted
    #     far enough that the source is clear of the pointer
    out = a.astype(float).copy()

    def valid(px_y, px_x):
        # A source may lie under the mask as long as it is not pointer: the
        # mask is deliberately generous and mostly covers plain face.
        ok = (px_y >= 0) & (px_y < H) & (px_x >= 0) & (px_x < W)
        yy2 = np.clip(px_y, 0, H - 1); xx2 = np.clip(px_x, 0, W - 1)
        return ok & ~light[yy2, xx2] & ~teal[yy2, xx2]

    def fill_from(region, src_y, src_x):
        """Copy source pixels into the region; report how many had no valid source."""
        ys, xs = np.nonzero(region)
        sy, sx = src_y[ys, xs], src_x[ys, xs]
        good = valid(sy, sx)
        out[ys[good], xs[good]] = a[sy[good], sx[good]]
        filled_any[ys[good], xs[good]] = True
        return int((~good).sum())

    # per-pointer regions
    regions = {}
    for name, (bx, by, br) in bosses.items():
        d = np.hypot(xx - bx, yy - by)
        ang = np.degrees(np.arctan2(yy - by, xx - bx))
        dang = np.abs(((ang - angles[name] + 180) % 360) - 180)
        regions[name] = mask & (dang < 40) & (d < 0.75 * R)

    filled_any = np.zeros((H, W), bool)

    # accumulator: mirror across the vertical axis of the face
    mx = np.round(2 * cx - xx).astype(int)
    miss = fill_from(regions["accum"], yy, mx)
    print("  accum fill: mirrored, %d px without a source" % miss)

    # Brakes: rotational copy around the boss.
    #
    # The window behind each boss is shaded radially - darker close to the
    # boss, lighter toward the scale - so any fill built from an average is
    # wrong somewhere. Copying each pixel from the SAME RADIUS on a clean
    # bearing keeps that gradient and the grain, with nothing synthesised.
    bright_all = a.mean(axis=2)
    boss_px = np.zeros((H, W), bool)
    for _, (bx, by, br) in bosses.items():
        boss_px |= np.hypot(xx - bx, yy - by) < br * 1.05

    for name in ("brake_l", "brake_r"):
        reg = regions[name]
        bx, by, br = bosses[name]

        # Sources must look like the window itself: within a narrow band of
        # its median tone. That excludes the darker border lines of the window
        # (which were being copied in as thin seams) as well as anything light.
        dd = np.hypot(xx - bx, yy - by)
        sample = ~mask & ~light & ~teal & ~boss_px & (dd > br * 1.1) & (dd < 0.5 * R)
        wmed = np.median(bright_all[sample])
        ok_src = sample & (np.abs(bright_all - wmed) < 8)

        ys, xs = np.nonzero(reg)
        r_ = np.hypot(xs - bx, ys - by)
        th = np.arctan2(ys - by, xs - bx)
        done = np.zeros(len(xs), bool)

        # alternate sides so the copy does not all come from one direction,
        # widening until every pixel has a clean source
        for delta_deg in (22, -22, 28, -28, 34, -34, 42, -42, 52, -52):
            if done.all():
                break
            d = np.radians(delta_deg)
            sx = np.round(bx + r_ * np.cos(th + d)).astype(int)
            sy = np.round(by + r_ * np.sin(th + d)).astype(int)
            inb = (sx >= 0) & (sx < W) & (sy >= 0) & (sy < H)
            good = ~done & inb
            good[good] &= ok_src[sy[good], sx[good]]
            out[ys[good], xs[good]] = a[sy[good], sx[good]]
            done |= good

        # unresolved pixels: inside the boss take the boss tone, elsewhere the
        # window tone - never a face-wide median, which shows as a pale blob
        left = ~done
        if left.any():
            boss_tone = np.median(a[(dd < br * 0.8) & ~light], axis=0)
            src_r = dd[ok_src]; src_v = a[ok_src]
            for i in np.nonzero(left)[0]:
                if r_[i] < br * 1.02:
                    out[ys[i], xs[i]] = boss_tone
                    continue
                band = np.abs(src_r - r_[i]) < R * 0.02
                out[ys[i], xs[i]] = (np.median(src_v[band], axis=0) if band.sum() > 20
                                     else np.median(src_v, axis=0))
        filled_any[ys, xs] = True
        print("  %s fill: rotational copy, %d of %d px by tone fallback"
              % (name, int(left.sum()), len(xs)))

    # anything the copies could not reach falls back to a local median
    leftover = mask & ~filled_any
    if leftover.any():
        out[leftover] = np.median(a[~mask & ~light & ~teal], axis=0)

    # feather the seam: blend the boundary ring between copy and original
    def dilate(m, n):
        o = m.copy()
        for _ in range(n):
            p = o.copy()
            p[1:, :] |= o[:-1, :]; p[:-1, :] |= o[1:, :]
            p[:, 1:] |= o[:, :-1]; p[:, :-1] |= o[:, 1:]
            o = p
        return o
    ring = dilate(mask, 2) & ~mask
    out[ring] = 0.5 * out[ring] + 0.5 * a[ring]

    out = np.clip(out, 0, 255)
    return out.astype(np.uint8), angles


def main():
    os.makedirs(OUT, exist_ok=True)
    im = Image.open(REF).convert("RGB")
    a = np.asarray(im).astype(np.int32)
    H, W, _ = a.shape

    cx, cy, R = find_circle(a)
    print("dial circle: centre (%.0f, %.0f) radius %.0f px" % (cx, cy, R))

    bosses = {
        "accum":   (cx + 0.00 * R, cy - 0.75 * R, 0.20 * R),
        "brake_l": (cx - 0.60 * R, cy + 0.48 * R, 0.20 * R),
        "brake_r": (cx + 0.60 * R, cy + 0.48 * R, 0.20 * R),
    }

    # Refine each boss centre from the pixels: it is the darkest disc in its
    # neighbourhood. The radius is NOT taken from the pixels - the dark
    # cutout window behind each boss inflates it - so it stays at the
    # measured proportion of the face radius.
    bright = a.mean(axis=2)
    yy, xx = np.mgrid[0:H, 0:W]
    refined = {}
    for name, (bx, by, br) in bosses.items():
        near = np.hypot(xx - bx, yy - by) < br * 1.3
        dark = near & (bright < 29)
        ys, xs = np.nonzero(dark)
        if len(xs) > 200:
            refined[name] = (xs.mean(), ys.mean(), br)
            print("  %-8s boss centre (%.0f,%.0f)" % (name, xs.mean(), ys.mean()))
        else:
            refined[name] = (bx, by, br)
    bosses = refined

    clean, found = lift_needles(a, cx, cy, R, bosses)
    for k, v in found.items():
        print("  removed %-8s pointer at %.0f deg" % (k, v))

    # crop to the circle, black outside, resample to the screen size
    yy, xx = np.mgrid[0:H, 0:W]
    inside = np.hypot(xx - cx, yy - cy) <= R - 3.0
    clean[~inside] = 0

    face = Image.fromarray(clean).crop(
        (int(cx - R), int(cy - R), int(cx + R), int(cy + R)))
    face = face.resize((SIZE, SIZE), Image.LANCZOS)

    # re-mask after resampling so the rim is a clean edge, not a soft one
    fa = np.asarray(face).copy()
    yy, xx = np.mgrid[0:SIZE, 0:SIZE]
    fa[np.hypot(xx - SIZE / 2, yy - SIZE / 2) > SIZE / 2 - 1] = 0
    face = Image.fromarray(fa)
    face.save(os.path.join(OUT, "dial_face.png"))
    print("wrote dial_face.png")

    # Geometry for the live pointers, in the 480 design space. Scale angles
    # were measured from the tick marks on this image and are piecewise
    # linear: the brake scales give 0-1 more arc than each unit of 1-3.
    import json
    k = SIZE / (2 * R)
    def px(v): return (v - (cx - R)) * k
    def py(v): return (v - (cy - R)) * k

    geom = {
        "face": "dial_face.png",
        "size": SIZE,
        "scales": {
            "accum": {
                "pivot": [round(px(bosses["accum"][0]), 1), round(py(bosses["accum"][1]), 1)],
                "boss_r": round(bosses["accum"][2] * k, 1),
                "unit": 1000.0,
                "points": [[0.0, 126.5], [4.0, 46.5]],
                "needle_len": round(0.60 * R * k, 1), "needle_w": round(0.095 * R * k, 1),
            },
            "brake_l": {
                "pivot": [round(px(bosses["brake_l"][0]), 1), round(py(bosses["brake_l"][1]), 1)],
                "boss_r": round(bosses["brake_l"][2] * k, 1),
                "unit": 1000.0,
                "points": [[0.0, -3.0], [1.0, -48.5], [3.0, -101.5]],
                "needle_len": round(0.43 * R * k, 1), "needle_w": round(0.10 * R * k, 1),
            },
            "brake_r": {
                "pivot": [round(px(bosses["brake_r"][0]), 1), round(py(bosses["brake_r"][1]), 1)],
                "boss_r": round(bosses["brake_r"][2] * k, 1),
                "unit": 1000.0,
                "points": [[0.0, 183.0], [1.0, 228.5], [3.0, 281.5]],
                "needle_len": round(0.43 * R * k, 1), "needle_w": round(0.10 * R * k, 1),
            },
        },
        "needle_colour": "#c3c7cb",
    }
    with open(os.path.join(OUT, "geometry.json"), "w") as fh:
        json.dump(geom, fh, indent=2)
    for n, g in geom["scales"].items():
        print("  %-8s pivot %s  boss r %s  points %s" % (n, g["pivot"], g["boss_r"], g["points"]))


if __name__ == "__main__":
    main()
