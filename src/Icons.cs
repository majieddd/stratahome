using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace StrataHome
{
    /// <summary>The tray icons: the same four-layer mark as the exe icon, tinted by what Strata is doing.</summary>
    internal static class Icons
    {
        static readonly Dictionary<RunState, Icon> cache = new Dictionary<RunState, Icon>();

        public static Icon For(RunState s)
        {
            lock (cache)
            {
                Icon i;
                if (!cache.TryGetValue(s, out i)) { i = Make(s); cache[s] = i; }
                return i;
            }
        }

        public static Color StateColor(RunState s)
        {
            switch (s)
            {
                case RunState.Ready:
                case RunState.External: return Color.FromArgb(63, 160, 140);
                case RunState.Unloaded: return Color.FromArgb(110, 150, 200);
                case RunState.Starting:
                case RunState.Loading:
                case RunState.Stopping: return Color.FromArgb(232, 176, 75);
                case RunState.Error: return Color.FromArgb(214, 84, 84);
                default: return Color.FromArgb(140, 145, 150);
            }
        }

        static Color[] Bands(RunState s)
        {
            switch (s)
            {
                case RunState.Ready:
                case RunState.External:
                    return new Color[] { Color.FromArgb(232, 176, 75), Color.FromArgb(214, 150, 80), Color.FromArgb(96, 168, 150), Color.FromArgb(63, 140, 160) };
                case RunState.Unloaded:
                    return new Color[] { Color.FromArgb(120, 150, 190), Color.FromArgb(105, 140, 185), Color.FromArgb(95, 130, 175), Color.FromArgb(80, 115, 160) };
                case RunState.Starting:
                case RunState.Loading:
                case RunState.Stopping:
                    return new Color[] { Color.FromArgb(240, 190, 90), Color.FromArgb(232, 176, 75), Color.FromArgb(214, 150, 60), Color.FromArgb(190, 125, 50) };
                case RunState.Error:
                    return new Color[] { Color.FromArgb(230, 110, 100), Color.FromArgb(214, 84, 84), Color.FromArgb(190, 70, 70), Color.FromArgb(160, 60, 60) };
                default:
                    return new Color[] { Color.FromArgb(150, 155, 160), Color.FromArgb(135, 140, 145), Color.FromArgb(120, 125, 130), Color.FromArgb(105, 110, 115) };
            }
        }

        static Icon Make(RunState s)
        {
            const int N = 32;
            using (Bitmap bmp = new Bitmap(N, N, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (GraphicsPath bg = Round(new RectangleF(0, 0, N - 1, N - 1), N * 0.22f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(20, 24, 28)))
                    g.FillPath(b, bg);

                Color[] bands = Bands(s);
                float pad = N * 0.19f, gap = N * 0.045f;
                float bh = (N - 2 * pad - 3 * gap) / 4f;
                float[] inset = new float[] { 0f, 0.07f, 0.03f, 0.10f };
                for (int i = 0; i < 4; i++)
                {
                    float x0 = pad + N * inset[i] * 0.6f;
                    float x1 = N - pad - N * inset[(i + 2) % 4] * 0.9f;
                    float y0 = pad + i * (bh + gap);
                    using (GraphicsPath p = Round(new RectangleF(x0, y0, x1 - x0, bh), bh * 0.42f))
                    using (SolidBrush b = new SolidBrush(bands[i]))
                        g.FillPath(b, p);
                }

                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { Native.DestroyIcon(h); }
            }
        }

        static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
