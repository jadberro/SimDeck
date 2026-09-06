# fonts

**Routed Gothic** by Darren Embry — SIL Open Font License 1.1.

A digitisation of the Gorton / Leroy engraving lettering that MS33558
specifies for aircraft panel markings. It is what the numerals and captions on
the real instrument are cut in, so the dial artwork and the on-screen gauge
both use it.

`LICENSE.md` is the OFL text and must stay beside the font files. The OFL
permits bundling and embedding freely; the only real restriction is that the
font may not be sold on its own.

The face is a hairline by design — it draws the centreline of a routed cut.
Engraved lettering has weight, so both renderers stroke the glyph outlines as
well as filling them (`STROKE_RATIO` in `make_dial.py`, `StrokeRatio` in
`GaugeControl.cs`). Change both together.

Source: https://webonastick.com/fonts/routed-gothic/
