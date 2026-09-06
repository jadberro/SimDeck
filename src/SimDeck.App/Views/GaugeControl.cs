using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SimDeck.App.Views;

/// <summary>
/// The A320 accumulator and brake pressure triple indicator.
///
/// The face is the real instrument artwork supplied without pointers
/// (artwork/from_clean.py), not a drawing. Texture, lettering
/// and weight are therefore the genuine article. Only the three pointers are
/// rendered here. Their pivots are the fitted centres of the three boss discs
/// (sub-pixel for the brakes) and the scale angles were measured from the
/// tick marks around those centres, so they read correctly against them.
///
/// Everything is laid out in a 480x480 design space and scaled to fit, and the
/// constants come straight from artwork/build/geometry.json - the panel
/// firmware will use the same file, so screen and hardware cannot drift.
/// </summary>
public sealed class GaugeControl : FrameworkElement
{
    private const double Design = 480.0;

    // ---- scales -----------------------------------------------------------
    //
    // Angles are piecewise linear. The brake scales on the real instrument
    // give the 0-1 range more arc than each unit of 1-3, and a straight-line
    // fit was a few degrees out at the "1" tick.
    private sealed record Scale(
        double Cx, double Cy, double BossR, double NeedleLen, double NeedleW,
        (double v, double deg)[] Points);

    // Generated from artwork/build/geometry.json - do not edit by hand.
    private static readonly Scale Accum = new(
        240.3, 95.6, 48.9, 102.4, 22.8,
        new[] { (0.0, 138.5), (4.0, 44.6) });

    private static readonly Scale BrakeL = new(
        93.5, 348.1, 47.1, 100.8, 24.0,
        new[] { (0.0, 1.2), (1.0, -46.0), (3.0, -103.0) });

    private static readonly Scale BrakeR = new(
        388.6, 348.6, 47.1, 100.8, 24.0,
        new[] { (0.0, 179.0), (1.0, 225.5), (3.0, 283.0) });

    private static readonly Brush NeedleFill = Rgb("#AEB2B6");
    private static readonly Brush NeedleEdge = Rgb("#585B5E");

    private static Brush Rgb(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }

    // ---- face -------------------------------------------------------------

    private static readonly ImageSource? FaceImage = LoadFace();

    private static ImageSource? LoadFace()
    {
        try
        {
            var img = new BitmapImage(new Uri(
                "pack://application:,,,/faces/accu_panel.png", UriKind.Absolute));
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    // ---- live values ------------------------------------------------------

    /// <summary>
    /// Called once per frame for fresh values. Pull rather than push: a timer
    /// pushing targets means the pointer chases a stale number and moves in
    /// steps.
    /// </summary>
    public Func<(double accum, double left, double right, bool live)>? Sample { get; set; }

    public double TargetAccum { get; set; }
    public double TargetLeft { get; set; }
    public double TargetRight { get; set; }

    /// <summary>False when there is no live data: pointers fall to zero rather
    /// than freezing mid-scale, which would read as a real pressure.</summary>
    public bool Live { get; set; }

    // Time constants, not per-frame factors. The firmware applies 0.12 and
    // 0.16 at a fixed 30Hz; expressed as time these are 0.26s and 0.19s, and
    // the pointer then behaves identically whatever the frame rate.
    private const double TauAccum = 0.26;
    private const double TauBrake = 0.19;

    private double _accum, _left, _right;
    private long _lastTick;

    public GaugeControl()
    {
        CompositionTarget.Rendering += (_, _) =>
        {
            // Pages are stacked and toggled by Visibility, so this control
            // exists even when another page is showing. Do not animate then.
            if (!IsVisible) return;

            var now = DateTime.UtcNow.Ticks;
            var dt = _lastTick == 0 ? 1.0 / 60 : (now - _lastTick) / 1e7;
            _lastTick = now;
            if (dt <= 0 || dt > 0.5) dt = 1.0 / 60;   // resumed after a stall

            if (Sample is not null)
            {
                var s = Sample();
                TargetAccum = s.accum; TargetLeft = s.left;
                TargetRight = s.right; Live = s.live;
            }

            var ta = Live ? TargetAccum : 0;
            var tl = Live ? TargetLeft : 0;
            var tr = Live ? TargetRight : 0;

            var ka = 1 - Math.Exp(-dt / TauAccum);
            var kb = 1 - Math.Exp(-dt / TauBrake);

            _accum += (ta - _accum) * ka;
            _left  += (tl - _left)  * kb;
            _right += (tr - _right) * kb;

            InvalidateVisual();
        };
    }

    protected override Size MeasureOverride(Size available)
    {
        var side = Math.Min(
            double.IsInfinity(available.Width) ? Design : available.Width,
            double.IsInfinity(available.Height) ? Design : available.Height);
        return new Size(side, side);
    }

    // ---- geometry helpers -------------------------------------------------

    /// <summary>Piecewise-linear value to degrees, clamped at the scale ends.</summary>
    private static double AngleFor(Scale s, double v)
    {
        var p = s.Points;
        if (v <= p[0].v) return p[0].deg;
        if (v >= p[^1].v) return p[^1].deg;
        for (int i = 0; i < p.Length - 1; i++)
        {
            var (v0, a0) = p[i];
            var (v1, a1) = p[i + 1];
            if (v >= v0 && v <= v1) return a0 + (v - v0) / (v1 - v0) * (a1 - a0);
        }
        return p[^1].deg;
    }

    private static StreamGeometry Poly(params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], true, true);
            for (int i = 1; i < pts.Length; i++) c.LineTo(pts[i], true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>
    /// The spade pointer: full width for two thirds of its length, then a
    /// short taper to a blunt tip about a sixth as wide. It starts inside the
    /// boss, so nothing ever shows behind the pivot. A one-pixel darker edge
    /// along the long sides sits it on the face rather than floating above it.
    /// </summary>
    private static void Needle(DrawingContext dc, Scale s, double valueK)
    {
        var deg = AngleFor(s, valueK);
        var ln = s.NeedleLen;
        var w = s.NeedleW;
        var root = s.BossR - 8.0;
        var shoulder = ln * 0.66;
        var tip = w * 0.16;
        const double e = 1.1;

        var outer = Poly(new Point(root, -w / 2), new Point(shoulder, -w / 2),
                         new Point(ln, -tip), new Point(ln, tip),
                         new Point(shoulder, w / 2), new Point(root, w / 2));
        var inner = Poly(new Point(root, -w / 2 + e), new Point(shoulder, -w / 2 + e),
                         new Point(ln - e, -tip + e * 0.4), new Point(ln - e, tip - e * 0.4),
                         new Point(shoulder, w / 2 - e), new Point(root, w / 2 - e));

        dc.PushTransform(new TranslateTransform(s.Cx, s.Cy));
        dc.PushTransform(new RotateTransform(deg));
        dc.DrawGeometry(NeedleEdge, null, outer);
        dc.DrawGeometry(NeedleFill, null, inner);
        dc.Pop();
        dc.Pop();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0) return;

        dc.PushTransform(new TranslateTransform(
            (ActualWidth - side) / 2, (ActualHeight - side) / 2));
        dc.PushTransform(new ScaleTransform(side / Design, side / Design));

        if (FaceImage is not null)
        {
            dc.DrawImage(FaceImage, new Rect(0, 0, Design, Design));
        }
        else
        {
            // The face resource failed to load. Draw something rather than
            // nothing, so the failure is obvious instead of a blank square.
            dc.DrawEllipse(Brushes.Black, new Pen(Brushes.DarkRed, 4),
                           new Point(240, 240), 236, 236);
        }

        // The dial reads in thousands of psi; the wire carries raw psi.
        Needle(dc, Accum, _accum / 1000.0);
        Needle(dc, BrakeL, _left / 1000.0);
        Needle(dc, BrakeR, _right / 1000.0);

        dc.Pop();
        dc.Pop();
    }
}
