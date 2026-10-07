using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StrataHome
{
    /// <summary>Saves a window as it really looks (used by --screenshot and --screenshot-chat).</summary>
    internal static class WindowShot
    {
        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        public static void Save(Form f, string path)
        {
            using (Bitmap bmp = new Bitmap(f.Width, f.Height))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { PrintWindow(f.Handle, hdc, 2); }          // 2 = PW_RENDERFULLCONTENT: includes the rich text box
                finally { g.ReleaseHdc(hdc); }
                bmp.Save(path, ImageFormat.Png);
            }
        }
    }
}

