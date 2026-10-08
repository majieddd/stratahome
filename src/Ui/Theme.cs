using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace StrataHome
{
    /// <summary>The Strata web app's colour tokens (serve/web/tokens.css), as resource brushes: light and dark.</summary>
    internal static class Theme
    {
        public static bool Dark;
        public static event Action Changed;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        static SolidColorBrush B(string hex)
        {
            SolidColorBrush b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        public static ResourceDictionary Make(bool dark)
        {
            ResourceDictionary d = new ResourceDictionary();
            string[][] light = new string[][]
            {
                new string[] { "StBg", "#F9FAFB" }, new string[] { "StSurface", "#FFFFFF" }, new string[] { "StSurface2", "#F7F7F7" },
                new string[] { "StLine", "#DEE5EE" }, new string[] { "StLineSoft", "#EEEEEE" },
                new string[] { "StInk", "#221E1F" }, new string[] { "StInkSoft", "#303030" }, new string[] { "StInkMuted", "#6F6F6F" },
                new string[] { "StAccent", "#10B981" }, new string[] { "StAccentHover", "#0C8A60" }, new string[] { "StAccentInk", "#FFFFFF" },
                new string[] { "StAccentTint", "#ECFDF5" }, new string[] { "StAccentText", "#0C8A60" }, new string[] { "StFocus", "#405DE6" },
                new string[] { "StInfo", "#0F97FF" }, new string[] { "StInfoTint", "#E8F4FF" }, new string[] { "StInfoText", "#0668C2" },
                new string[] { "StWarn", "#E39B0B" }, new string[] { "StWarnTint", "#FFF6E0" }, new string[] { "StWarnText", "#8A5A00" },
                new string[] { "StDanger", "#E0283F" }, new string[] { "StDangerTint", "#FDECEE" }, new string[] { "StDangerText", "#B01A2E" },
                new string[] { "StCodeBg", "#1B1F23" }, new string[] { "StCodeInk", "#E6E9EC" }, new string[] { "StScrim", "#2E0F172A" }
            };
            string[][] night = new string[][]
            {
                new string[] { "StBg", "#0E1113" }, new string[] { "StSurface", "#161A1D" }, new string[] { "StSurface2", "#1D2226" },
                new string[] { "StLine", "#2A3036" }, new string[] { "StLineSoft", "#22272C" },
                new string[] { "StInk", "#EEF1F3" }, new string[] { "StInkSoft", "#C7CDD3" }, new string[] { "StInkMuted", "#8E979F" },
                new string[] { "StAccent", "#34D399" }, new string[] { "StAccentHover", "#4ADE80" }, new string[] { "StAccentInk", "#052E20" },
                new string[] { "StAccentTint", "#1F34D399" }, new string[] { "StAccentText", "#4ADE80" }, new string[] { "StFocus", "#7B93FF" },
                new string[] { "StInfo", "#3AA9FF" }, new string[] { "StInfoTint", "#1F3AA9FF" }, new string[] { "StInfoText", "#7CC4FF" },
                new string[] { "StWarn", "#F5B53D" }, new string[] { "StWarnTint", "#1FF5B53D" }, new string[] { "StWarnText", "#F8C96A" },
                new string[] { "StDanger", "#FF5A6E" }, new string[] { "StDangerTint", "#1FFF5A6E" }, new string[] { "StDangerText", "#FF8A98" },
                new string[] { "StCodeBg", "#0A0C0E" }, new string[] { "StCodeInk", "#E6E9EC" }, new string[] { "StScrim", "#59000000" }
            };
            foreach (string[] kv in dark ? night : light) d[kv[0]] = B(kv[1]);
            return d;
        }

        public static Brush Get(string key)
        {
            return (Brush)Application.Current.FindResource(key);
        }

        /// <summary>Swaps the colour dictionary (always the first merged one) and tells the custom-drawn controls.</summary>
        public static void Apply(bool dark)
        {
            Dark = dark;
            ResourceDictionary rd = Application.Current.Resources;
            if (rd.MergedDictionaries.Count == 0) rd.MergedDictionaries.Add(Make(dark));
            else rd.MergedDictionaries[0] = Make(dark);
            foreach (Window w in Application.Current.Windows) TitleBar(w);
            Action c = Changed;
            if (c != null) c();
        }

        /// <summary>Dark or light caption to match the theme (Windows 10 1809+; ignored where unsupported).</summary>
        public static void TitleBar(Window w)
        {
            try
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                int on = Dark ? 1 : 0;
                if (DwmSetWindowAttribute(h, 20, ref on, 4) != 0) DwmSetWindowAttribute(h, 19, ref on, 4);
            }
            catch { }
        }
    }
}
