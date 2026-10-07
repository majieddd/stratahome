using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace StrataHome
{
    /// <summary>Saves a window as it really looks, title bar included (used by --screenshot).</summary>
    internal static class WindowShot
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Rect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out Rect r);

        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        public static void Save(IntPtr hwnd, string path)
        {
            Rect r;
            GetWindowRect(hwnd, out r);
            int w = Math.Max(1, r.Right - r.Left), h = Math.Max(1, r.Bottom - r.Top);
            using (Bitmap bmp = new Bitmap(w, h))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { PrintWindow(hwnd, hdc, 2); }              // 2 = PW_RENDERFULLCONTENT: includes DWM-composed content
                finally { g.ReleaseHdc(hdc); }
                bmp.Save(path, ImageFormat.Png);
            }
        }
    }
}
