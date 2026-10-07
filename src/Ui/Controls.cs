using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace StrataHome
{
    /// <summary>Resolves a theme brush at draw time, so a theme swap only needs a repaint.</summary>
    internal static class Res
    {
        public static Brush Brush(FrameworkElement owner, string key, Brush fallback)
        {
            Brush b = owner.TryFindResource(key) as Brush;
            return b ?? fallback;
        }

        public static Color ColorOf(Brush b, Color fallback)
        {
            SolidColorBrush s = b as SolidColorBrush;
            return s != null ? s.Color : fallback;
        }
    }

    /// <summary>One of Strata's duotone icons (24 x 24: a 12% fill layer plus 1.5-wide strokes) in the inherited text colour.</summary>
    internal sealed class IconView : FrameworkElement
    {
        static readonly Dictionary<string, Geometry[][]> cache = new Dictionary<string, Geometry[][]>();
        readonly Geometry[][] parts;

        public IconView(string name, double size)
        {
            Width = Height = size;
            IsHitTestVisible = false;
            Geometry[][] g;
            if (!cache.TryGetValue(name, out g))
            {
                string[][] data;
                if (!IconData.All.TryGetValue(name, out data)) data = new string[][] { new string[0], new string[0] };
                g = new Geometry[2][];
                for (int k = 0; k < 2; k++)
                {
                    g[k] = new Geometry[data[k].Length];
                    for (int i = 0; i < data[k].Length; i++) { g[k][i] = Geometry.Parse(data[k][i]); g[k][i].Freeze(); }
                }
                cache[name] = g;
            }
            parts = g;
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == System.Windows.Documents.TextElement.ForegroundProperty) InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            Brush brush = System.Windows.Documents.TextElement.GetForeground(this) ?? Brushes.Gray;
            double s = ActualWidth / 24.0;
            dc.PushTransform(new ScaleTransform(s, s));
            dc.PushOpacity(0.12);
            foreach (Geometry g in parts[0]) dc.DrawGeometry(brush, null, g);
            dc.Pop();
            Pen pen = new Pen(brush, 1.5);
            pen.StartLineCap = PenLineCap.Round; pen.EndLineCap = PenLineCap.Round; pen.LineJoin = PenLineJoin.Round;
            foreach (Geometry g in parts[1]) dc.DrawGeometry(null, pen, g);
            dc.Pop();
        }
    }

    /// <summary>The Monitor tiles' little history graphs: 60 samples, an area at 12% under a 1.6 px line.</summary>
    internal sealed class Sparkline : FrameworkElement
    {
        double[] a, b;
        double max;
        readonly string tone;

        public Sparkline(string tone)
        {
            this.tone = tone;
            Height = 32;
            IsHitTestVisible = false;
            Theme.Changed += delegate { InvalidateVisual(); };
        }

        public void Set(IList<double> primary, IList<double> secondary, double maxValue)
        {
            a = primary == null ? null : new List<double>(primary).ToArray();
            b = secondary == null ? null : new List<double>(secondary).ToArray();
            max = maxValue;
            InvalidateVisual();
        }

        Brush ToneBrush(bool second)
        {
            string key = tone == "warn" ? "StWarn" : tone == "info" ? "StInfo" : "StAccent";
            Brush br = Res.Brush(this, key, Brushes.SeaGreen);
            if (second)
            {
                Color c = Res.ColorOf(br, Colors.SeaGreen);                 // color-mix(accent 65%, black)
                br = new SolidColorBrush(Color.FromRgb((byte)(c.R * 0.65), (byte)(c.G * 0.65), (byte)(c.B * 0.65)));
            }
            return br;
        }

        void Draw(DrawingContext dc, double[] v, bool second, double top)
        {
            if (v == null || v.Length < 2) return;
            double w = ActualWidth, h = ActualHeight;
            Point[] pts = new Point[v.Length];
            for (int i = 0; i < v.Length; i++) pts[i] = new Point(i / (double)(v.Length - 1) * w, h * (30 - v[i] / top * 26) / 32.0);
            Brush br = ToneBrush(second);
            StreamGeometry area = new StreamGeometry();
            using (StreamGeometryContext c = area.Open())
            {
                c.BeginFigure(new Point(0, h), true, true);
                foreach (Point p in pts) c.LineTo(p, true, false);
                c.LineTo(new Point(w, h), true, false);
            }
            area.Freeze();
            dc.PushOpacity(0.12); dc.DrawGeometry(br, null, area); dc.Pop();
            StreamGeometry line = new StreamGeometry();
            using (StreamGeometryContext c = line.Open())
            {
                c.BeginFigure(pts[0], false, false);
                for (int i = 1; i < pts.Length; i++) c.LineTo(pts[i], true, true);
            }
            line.Freeze();
            Pen pen = new Pen(br, 1.6); pen.LineJoin = PenLineJoin.Round; pen.StartLineCap = PenLineCap.Round; pen.EndLineCap = PenLineCap.Round;
            dc.DrawGeometry(null, pen, line);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double top = 1e-9;
            top = Math.Max(top, max);
            if (a != null) foreach (double x in a) top = Math.Max(top, x);
            Draw(dc, a, false, top);
            // the prefill line shares the tile: drawn on its own scale
            if (b != null && b.Length > 1)
            {
                double top2 = 1e-9;
                foreach (double x in b) top2 = Math.Max(top2, x);
                Draw(dc, b, true, top2);
            }
        }
    }

    /// <summary>The context-fill gauge: a 270 degree track from the lower left round to the lower right.</summary>
    internal sealed class GaugeView : FrameworkElement
    {
        double fraction;

        public GaugeView(double size)
        {
            Width = Height = size;
            IsHitTestVisible = false;
            Theme.Changed += delegate { InvalidateVisual(); };
        }

        public void Set(double f)
        {
            fraction = Math.Max(0, Math.Min(1, f));
            InvalidateVisual();
        }

        static Point At(Point c, double r, double deg)
        {
            double a = deg * Math.PI / 180.0;
            return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }

        static Geometry Arc(Point c, double r, double fromDeg, double sweepDeg)
        {
            StreamGeometry g = new StreamGeometry();
            using (StreamGeometryContext ctx = g.Open())
            {
                ctx.BeginFigure(At(c, r, fromDeg), false, false);
                ctx.ArcTo(At(c, r, fromDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, true);
            }
            g.Freeze();
            return g;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            double k = size / 120.0;
            Point c = new Point(ActualWidth / 2, ActualHeight / 2);
            double r = 50 * k, sw = 10 * k;
            Pen track = new Pen(Res.Brush(this, "StSurface2", Brushes.LightGray), sw);
            track.StartLineCap = PenLineCap.Round; track.EndLineCap = PenLineCap.Round;
            dc.DrawGeometry(null, track, Arc(c, r, 135, 269.9));
            if (235.6 * fraction >= 3)
            {
                Pen fill = new Pen(Res.Brush(this, "StAccent", Brushes.SeaGreen), sw);
                fill.StartLineCap = PenLineCap.Round; fill.EndLineCap = PenLineCap.Round;
                dc.DrawGeometry(null, fill, Arc(c, r, 135, 270 * fraction));
            }
        }
    }

    /// <summary>The 8 px progress bars (state card, experts in VRAM, RAM, temperature, conversation cache).</summary>
    internal sealed class BarView : FrameworkElement
    {
        double fraction;
        string tone = "accent";

        public BarView()
        {
            Height = 8;
            IsHitTestVisible = false;
            Theme.Changed += delegate { InvalidateVisual(); };
        }

        public void Set(double f, string toneName)
        {
            fraction = Math.Max(0, Math.Min(1, f));
            tone = toneName ?? "accent";
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 4) return;
            Pen edge = new Pen(Res.Brush(this, "StLineSoft", Brushes.Gainsboro), 1);
            dc.DrawRoundedRectangle(Res.Brush(this, "StSurface2", Brushes.WhiteSmoke), edge, new Rect(0.5, 0.5, w - 1, h - 1), h / 2, h / 2);
            double fw = fraction * (w - 2);
            if (fw < 1) return;
            string key = tone == "info" ? "StInfo" : tone == "warn" ? "StWarn" : tone == "danger" ? "StDanger" : "StAccent";
            Rect r = new Rect(1, 1, Math.Max(h - 2, fw), h - 2);
            dc.PushClip(new RectangleGeometry(new Rect(1, 1, w - 2, h - 2), (h - 2) / 2, (h - 2) / 2));
            dc.DrawRoundedRectangle(Res.Brush(this, key, Brushes.SeaGreen), null, r, (h - 2) / 2, (h - 2) / 2);
            dc.Pop();
        }
    }
}
