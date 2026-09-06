#!/usr/bin/env python3
"""Build the dial face from the clean, pointer-free artwork (brakes.svg).

  1. face centre by RIM SYMMETRY  - the bezel's highlight is uneven, so an
     edge-sharpness fit lands 23px off; at the true centre a ring just inside
     the edge is equally bright all round
  2. face radius from the radial profile - where the face tone starts rising
     into the bezel lip; crop inside that so no lip bleeds in
  3. boss centres by CIRCLE FIT to each disc's edge against the black window
     - a centroid is biased wherever the disc meets the face
  4. scale angles re-measured from the ticks around those centres
"""
import json, math, os
import numpy as np
import cairosvg
from PIL import Image
from collections import deque

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "build")
SRC = os.path.join(HERE, "source", "brakes.svg")
import sys
SIZE = int(sys.argv[1]) if len(sys.argv) > 1 else 480
RASTER_W = 2400


def rim_symmetric_centre(br, guess, R0):
    H, W = br.shape
    yy, xx = np.mgrid[0:H, 0:W]
    best = None
    for cx in range(guess[0] - 18, guess[0] + 19, 3):
        for cy in range(guess[1] - 18, guess[1] + 19, 3):
            d = np.hypot(xx - cx, yy - cy); ang = np.arctan2(yy - cy, xx - cx)
            ring = (d > R0 - 40) & (d < R0 - 12)
            sect = ((ang + np.pi) / (2 * np.pi) * 12).astype(int) % 12
            s = np.array([br[ring & (sect == k)].mean() for k in range(12)]).std()
            if best is None or s < best[0]: best = (s, cx, cy)
    return best[1], best[2]


def face_radius(br, cx, cy, R0):
    H, W = br.shape
    yy, xx = np.mgrid[0:H, 0:W]
    d = np.hypot(xx - cx, yy - cy)
    prof = {r: br[(d >= r) & (d < r + 3)].mean() for r in range(int(R0 * 0.9), int(R0 * 1.05), 2)}
    base = np.median([v for r, v in prof.items() if r < R0 * 0.94])
    for r in sorted(prof):
        if prof[r] > base + 5: return r - 4
    return R0


def flood(br, seed, lo, hi, maxr):
    H, W = br.shape
    sx, sy = seed; x0, x1 = max(0, sx - maxr), min(W, sx + maxr); y0, y1 = max(0, sy - maxr), min(H, sy + maxr)
    sub = br[y0:y1, x0:x1]; ok = (sub > lo) & (sub < hi); seen = np.zeros_like(ok)
    q = deque([(sy - y0, sx - x0)]); seen[sy - y0, sx - x0] = True
    while q:
        y, x = q.popleft()
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            ny, nx = y + dy, x + dx
            if 0 <= ny < ok.shape[0] and 0 <= nx < ok.shape[1] and ok[ny, nx] and not seen[ny, nx]:
                seen[ny, nx] = True; q.append((ny, nx))
    m = np.zeros((H, W), bool); ys, xs = np.nonzero(seen); m[ys + y0, xs + x0] = True
    return m


def fit_circle(xs, ys):
    A = np.column_stack([xs, ys, np.ones_like(xs)]).astype(float)
    b = -(xs.astype(float) ** 2 + ys.astype(float) ** 2)
    D, E, F = np.linalg.lstsq(A, b, rcond=None)[0]
    xc, yc = -D / 2, -E / 2
    return float(xc), float(yc), float(np.sqrt(xc * xc + yc * yc - F))


def boss_circle(br, seed, R):
    m = flood(br, seed, 28, 70, int(0.26 * R))
    dark = br < 25
    edge = np.zeros_like(m)
    edge[1:, :] |= m[1:, :] & dark[:-1, :]; edge[:-1, :] |= m[:-1, :] & dark[1:, :]
    edge[:, 1:] |= m[:, 1:] & dark[:, :-1]; edge[:, :-1] |= m[:, :-1] & dark[:, 1:]
    ey, ex = np.nonzero(edge)
    xc, yc, r = fit_circle(ex, ey)
    rms = float(np.abs(np.hypot(ex - xc, ey - yc) - r).std())
    return xc, yc, r, rms


def extents(mask, xx, yy, px, py, rlo, rhi, R, gap=2.0, minpx=60):
    d = np.hypot(xx - px, yy - py); sel = mask & (d > rlo * R) & (d < rhi * R)
    ys, xs = np.nonzero(sel); ang = np.sort(np.degrees(np.arctan2(ys - py, xs - px)))
    if len(ang) == 0: return []
    groups = [[ang[0]]]
    for v in ang[1:]:
        (groups[-1].append(v) if v - groups[-1][-1] <= gap else groups.append([v]))
    return [(round(float(g[0]), 1), round(float(g[-1]), 1), len(g)) for g in groups if len(g) > minpx]


def main():
    os.makedirs(OUT, exist_ok=True)
    raster = os.path.join(OUT, "clean_raster.png")
    if not os.path.exists(raster):
        cairosvg.svg2png(url=SRC, write_to=raster, output_width=RASTER_W)
    a = np.asarray(Image.open(raster).convert("RGB")).astype(int)
    H, W, _ = a.shape
    br = a.mean(axis=2)
    r, g, b = a[..., 0], a[..., 1], a[..., 2]
    light = (np.abs(r - b) < 30) & (np.abs(r - g) < 30) & (br > 95)
    teal = (g > 110) & (r < 90) & (b > 100)
    yy, xx = np.mgrid[0:H, 0:W]

    R0 = int(W * 0.375)
    cx, cy = rim_symmetric_centre(br, (W // 2, H // 2), R0)
    R = face_radius(br, cx, cy, R0)
    print("face: centre (%d,%d)  radius %d  (raster %dx%d)" % (cx, cy, R, W, H))

    bosses = {}
    for name, (fx, fy) in {"accum": (0.0, -0.585), "brake_l": (-0.59, 0.43), "brake_r": (0.59, 0.43)}.items():
        xc, yc, rr, rms = boss_circle(br, (int(cx + fx * R), int(cy + fy * R)), R)
        bosses[name] = (xc, yc, rr)
        print("  %-8s centre (%.1f,%.1f) r %.1f  fit rms %.2fpx  = (%.3f,%.3f)R" % (name, xc, yc, rr, rms, (xc - cx) / R, (yc - cy) / R))

    print("scale ticks (angles from each boss centre):")
    meas = {}
    for name, (bx, by, brr) in bosses.items():
        if name == "accum":
            meas[name] = dict(ticks=extents(light, xx, yy, bx, by, 0.60, 0.74, R), teal=extents(teal, xx, yy, bx, by, 0.50, 0.74, R, 6))
        else:
            meas[name] = dict(ticks=extents(light, xx, yy, bx, by, 0.44, 0.60, R), teal=extents(teal, xx, yy, bx, by, 0.34, 0.56, R, 6))
        print("  %-8s ticks %s" % (name, meas[name]["ticks"]))
        print("           teal  %s" % meas[name]["teal"])

    # crop onto black with a soft 2px edge, then downsample once
    out = a.astype(float)
    d0 = np.hypot(xx - cx, yy - cy)
    fade = np.clip((R - d0) / 2.0, 0, 1)[..., None]
    out = out * fade
    face = Image.fromarray(out.astype(np.uint8)).crop((int(cx - R), int(cy - R), int(cx + R), int(cy + R)))
    face = face.resize((SIZE, SIZE), Image.LANCZOS)
    fa = np.asarray(face).astype(float)
    yy2, xx2 = np.mgrid[0:SIZE, 0:SIZE]
    d2 = np.hypot(xx2 - SIZE / 2 + 0.5, yy2 - SIZE / 2 + 0.5)
    fa *= np.clip((SIZE / 2 - 0.5 - d2) / 1.5, 0, 1)[..., None]
    suffix = "" if SIZE == 480 else f"_{SIZE}"
    Image.fromarray(fa.astype(np.uint8)).save(os.path.join(OUT, f"dial_face{suffix}.png"))
    print(f"wrote dial_face{suffix}.png")

    k = SIZE / (2 * R)
    json.dump({
        "face": {"cx": int(cx), "cy": int(cy), "R": int(R)},
        "bosses": {n: [round((bx - (cx - R)) * k, 1), round((by - (cy - R)) * k, 1), round(brr * k, 1)]
                   for n, (bx, by, brr) in bosses.items()},
        "measurements": meas,
    }, open(os.path.join(OUT, f"clean_measure{suffix}.json"), "w"), indent=1)


if __name__ == "__main__":
    main()
