# Artwork pipeline

Everything under `artwork/`. Python 3 with Pillow, NumPy and cairosvg.

## The face

`from_clean.py [size]` - **the current pipeline.** Rasterises the supplied
pointer-free `brakes.svg` at 2400 px, finds the face circle by rim symmetry,
finds each boss by circle-fitting its edge against the black window, measures
tick angles, crops the face onto black with a soft edge, and writes:

- `build/dial_face.png` (480) or `build/dial_face_360.png`
- `build/clean_measure.json` - raw measurements

`build/geometry.json` (and `geometry_360.json`) hold the pivots, boss radii,
piecewise scale breakpoints and pointer dimensions. **This is the single
source of truth** for both the WPF preview and the firmware. The C# constants
in `GaugeControl.cs` are generated from it, not typed.

`compose.py a l r` - composites pointers onto the face at given values, the
same operation the app and firmware perform. Use it to check calibration:
`3.0 1.0 1.0` should land on the teal marker and both "1" ticks.

## Superseded but kept

- `from_reference.py` - extraction from a photo with painted pointers,
  including inpainting. Worked, never quite clean. Replaced by clean artwork.
- `make_dial.py` / `preview.py` / `verify_render.py` - the vector redraw.
  Still emits `gauge_geometry.h` and is the fallback if no artwork exists.
- `to_lvgl.py` - PNG → LVGL 8 C arrays (RGB565, alpha for pointers).
- `make_gif.py` - animated demo from the real assets.

## Measured geometry (480 space, 240 = centre)

| Scale | Pivot | Boss r | Points (value → degrees) |
|---|---|---|---|
| accumulator | 240.3, 95.6 | 48.9 | 0→138.5, 4→44.6 |
| brake left | 93.5, 348.1 | 47.1 | 0→1.2, 1→−46.0, 3→−103.0 |
| brake right | 388.6, 348.6 | 47.1 | 0→179.0, 1→225.5, 3→283.0 |

Angles: 0° = east, clockwise positive, y down. Left and right mirror within
a degree at every breakpoint. The teal accumulator marker sits at ~3.25.

## Fonts

`fonts/routed-gothic*.ttf` - SIL OFL 1.1, licence text alongside. Used by the
drawn fallback only.
