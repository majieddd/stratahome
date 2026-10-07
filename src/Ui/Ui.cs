using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace StrataHome
{
    /// <summary>Number formatting the way the web app does it (app.js: fmt, kfmt, ctxfmt, gb).</summary>
    internal static class Fmt
    {
        static readonly CultureInfo Ci = CultureInfo.CurrentCulture;

        public static string N(double? v, int decimals)
        {
            if (v == null || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return "\u2013";
            return v.Value.ToString("N" + decimals, Ci);
        }

        public static string N(double? v) { return N(v, 0); }

        public static string K(double? n)
        {
            if (n == null) return "\u2013";
            if (n >= 1000) return N(n / 1000, n >= 10000 ? 0 : 1) + "k";
            return N(n);
        }

        public static string Ctx(double? n)
        {
            if (n != null && n > 0 && Math.Abs(n.Value % 1024) < 0.5) return N(n / 1024) + "K";
            return K(n);
        }

        /// <summary>Memory in binary GB, as Windows shows it.</summary>
        public static string GB(double? bytes, int decimals)
        {
            return bytes == null ? "\u2013" : N(bytes / 1073741824.0, decimals);
        }

        public static string GB(double? bytes) { return GB(bytes, 1); }
    }

    /// <summary>Small factories so the pages read like the web app's markup.</summary>
    internal static class Ui
    {
        public static Style Style(string key) { return (Style)Application.Current.FindResource(key); }

        public static TextBlock Text(string text, double size, string brush, string weight, bool wrap)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = size;
            switch (weight)
            {
                case "medium": t.FontFamily = Fonts.Medium; break;
                case "black": t.FontFamily = Fonts.Black; break;
                case "bold": t.FontFamily = Fonts.Regular; t.FontWeight = FontWeights.Bold; break;
                default: t.FontFamily = Fonts.Regular; break;
            }
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            if (wrap) t.TextWrapping = TextWrapping.Wrap;
            return t;
        }

        public static TextBlock Text(string text, double size, string brush, string weight) { return Text(text, size, brush, weight, false); }

        public static TextBlock Text(string text, double size, string brush) { return Text(text, size, brush, "regular", false); }

        public static TextBlock Mono(string text, double size, string brush)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = size;
            t.FontFamily = Fonts.Mono;
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }

        public static Border Card(UIElement child, double padding)
        {
            Border b = new Border();
            b.Style = Style("StCard");
            b.Padding = new Thickness(padding);
            b.Child = child;
            return b;
        }

        public static Border Card(UIElement child) { return Card(child, 20); }

        public static IconView Icon(string name, double size) { return new IconView(name, size); }

        public static Button IconButton(string icon, string tip, RoutedEventHandler click)
        {
            Button b = new Button();
            b.Style = Style("StBtnIcon");
            b.Content = new IconView(icon, 20);
            b.ToolTip = tip;
            if (click != null) b.Click += click;
            return b;
        }

        /// <summary>kind: primary | secondary | danger.</summary>
        public static Button Btn(string kind, string label, string icon, RoutedEventHandler click)
        {
            Button b = new Button();
            b.Style = Style(kind == "primary" ? "StBtnPrimary" : kind == "danger" ? "StBtnDanger" : "StBtnSecondary");
            if (icon == null) b.Content = label;
            else
            {
                StackPanel sp = new StackPanel();
                sp.Orientation = Orientation.Horizontal;
                IconView iv = new IconView(icon, 16);
                iv.Margin = new Thickness(0, 0, label.Length > 0 ? 8 : 0, 0);
                iv.VerticalAlignment = VerticalAlignment.Center;
                sp.Children.Add(iv);
                if (label.Length > 0) { TextBlock t = new TextBlock(); t.Text = label; t.VerticalAlignment = VerticalAlignment.Center; sp.Children.Add(t); }
                b.Content = sp;
            }
            if (click != null) b.Click += click;
            return b;
        }

        public static ToggleButton Toggle(bool on, Action<bool> changed)
        {
            ToggleButton t = new ToggleButton();
            t.Style = Style("StToggle");
            t.IsChecked = on;
            if (changed != null) t.Click += delegate { changed(t.IsChecked == true); };
            return t;
        }

        /// <summary>The web app's badge: a pill with a dot. kind: idle | reading | generating | queued | error | done.</summary>
        public static Border Badge(string text, string kind)
        {
            string bg = "StSurface2", fg = "StInkMuted";
            bool edge = true;
            switch (kind)
            {
                case "reading": bg = "StInfoTint"; fg = "StInfoText"; edge = false; break;
                case "generating": bg = "StAccentTint"; fg = "StAccentText"; edge = false; break;
                case "queued": bg = "StWarnTint"; fg = "StWarnText"; edge = false; break;
                case "error": bg = "StDangerTint"; fg = "StDangerText"; edge = false; break;
            }
            Border b = new Border();
            b.Height = 24;
            b.CornerRadius = new CornerRadius(12);
            b.Padding = new Thickness(10, 0, 10, 0);
            b.SetResourceReference(Border.BackgroundProperty, bg);
            if (edge) { b.BorderThickness = new Thickness(1); b.SetResourceReference(Border.BorderBrushProperty, "StLine"); }
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            Ellipse dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(Shape.FillProperty, fg);
            sp.Children.Add(dot);
            TextBlock t = Text(text, 12, fg, "bold");
            t.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(t);
            b.Child = sp;
            return b;
        }

        public static void Copy(string text)
        {
            try { Clipboard.SetText(text); }
            catch { try { System.Threading.Thread.Sleep(60); Clipboard.SetText(text); } catch { } }
        }

        public static RowDefinition Row(GridLength h) { RowDefinition r = new RowDefinition(); r.Height = h; return r; }

        public static ColumnDefinition Col(GridLength w) { ColumnDefinition c = new ColumnDefinition(); c.Width = w; return c; }

        public static GridLength Star(double v) { return new GridLength(v, GridUnitType.Star); }

        public static GridLength Px(double v) { return new GridLength(v, GridUnitType.Pixel); }

        public static GridLength Auto { get { return GridLength.Auto; } }

        public static T At<T>(T el, int row, int col) where T : UIElement
        {
            Grid.SetRow(el, row);
            Grid.SetColumn(el, col);
            return el;
        }

        /// <summary>The dt / dd list of the About page: a label column and a value column, optionally with a copy button.</summary>
        public static Grid Facts(IList<string[]> rows)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(Col(new GridLength(160)));
            g.ColumnDefinitions.Add(Col(Star(1)));
            int r = 0;
            foreach (string[] row in rows)
            {
                if (row[1] == null || row[1].Length == 0) continue;
                g.RowDefinitions.Add(Row(Auto));
                TextBlock k = Text(row[0], 14, "StInkMuted");
                k.Margin = new Thickness(0, 5, 12, 5);
                k.VerticalAlignment = VerticalAlignment.Top;
                g.Children.Add(At(k, r, 0));
                StackPanel v = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 0) };
                bool copy = row.Length > 2 && row[2] == "copy";
                TextBlock val = copy ? Mono(row[1], 13, "StInkSoft") : Text(row[1], 14, "StInkSoft", "regular", true);
                val.VerticalAlignment = VerticalAlignment.Center;
                val.Margin = new Thickness(0, 5, 0, 5);
                if (!copy) { val.MaxWidth = 560; }
                v.Children.Add(val);
                if (copy)
                {
                    string copyText = row[1];
                    Button cb = IconButton("copy", "Copy", delegate { Copy(copyText); });
                    cb.Width = cb.Height = 28;
                    cb.Margin = new Thickness(6, 0, 0, 0);
                    v.Children.Add(cb);
                }
                g.Children.Add(At(v, r, 1));
                r++;
            }
            return g;
        }
    }
}
