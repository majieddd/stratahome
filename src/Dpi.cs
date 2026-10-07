using System;
using System.Drawing;
using System.Windows.Forms;

namespace StrataHome
{
    /// <summary>
    /// The windows are laid out in pixels at 96 dpi. Fonts already follow the display scale (they are sized in points),
    /// so on a 125% or 150% display the pixels have to follow too, or labels and buttons clip.
    /// </summary>
    internal static class Dpi
    {
        static float factor;

        public static float Factor
        {
            get
            {
                if (factor <= 0f)
                {
                    try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) factor = g.DpiX / 96f; }
                    catch { factor = 1f; }
                }
                return factor;
            }
        }

        public static void Apply(Form f)
        {
            float k = Factor;
            if (k < 1.01f) return;
            SizeF s = new SizeF(k, k);
            foreach (Control c in f.Controls) c.Scale(s);
            f.ClientSize = new Size((int)Math.Round(f.ClientSize.Width * k), (int)Math.Round(f.ClientSize.Height * k));
            f.MinimumSize = new Size((int)Math.Round(f.MinimumSize.Width * k), (int)Math.Round(f.MinimumSize.Height * k));
        }
    }
}
