using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;

namespace StrataHome
{
    /// <summary>
    /// Outfit, the Strata web app's font (SIL OFL 1.1, assets/fonts/OFL.txt). WPF cannot use the web app's woff2 variable
    /// font, so four static weights are embedded in the exe, copied once to %LOCALAPPDATA%\StrataHome\fonts, and loaded
    /// from there. If that fails the UI falls back to Segoe UI.
    /// </summary>
    internal static class Fonts
    {
        public static FontFamily Regular, Medium, Black, Mono;
        public static bool Loaded;

        public static void Init()
        {
            Mono = new FontFamily("Cascadia Mono, Consolas, Courier New");
            Regular = Medium = Black = new FontFamily("Segoe UI Variable Text, Segoe UI");
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StrataHome", "fonts");
                Directory.CreateDirectory(dir);
                Assembly asm = Assembly.GetExecutingAssembly();
                foreach (string name in new string[] { "Regular", "Medium", "Bold", "Black" })
                {
                    string file = Path.Combine(dir, "Outfit-" + name + ".ttf");
                    using (Stream s = asm.GetManifestResourceStream("Fonts.Outfit-" + name + ".ttf"))
                    {
                        if (s == null) throw new FileNotFoundException("embedded font missing: " + name);
                        if (!File.Exists(file) || new FileInfo(file).Length != s.Length)
                            using (FileStream f = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read)) s.CopyTo(f);
                    }
                }
                Uri baseUri = new Uri(dir + Path.DirectorySeparatorChar);
                Regular = new FontFamily(baseUri, "./#Outfit");
                Medium = new FontFamily(baseUri, "./#Outfit Medium");
                Black = new FontFamily(baseUri, "./#Outfit Black");
                Loaded = true;
            }
            catch (Exception ex) { Paths.Diag("fonts: " + ex.Message); }

            ResourceDictionary r = Application.Current.Resources;
            r["StFont"] = Regular;
            r["StFontMedium"] = Medium;
            r["StFontBlack"] = Black;
            r["StFontMono"] = Mono;
        }
    }
}
