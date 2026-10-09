using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace StrataHome
{
    /// <summary>The web app's Sampling drawer: thinking level, temperature, top-p, top-k, max tokens, seed, and the switches.</summary>
    internal sealed class SamplingDrawer
    {
        public readonly FrameworkElement Root;
        readonly MainWindow w;
        readonly Border scrim = new Border();
        readonly Border panel = new Border();
        readonly TranslateTransform slide = new TranslateTransform(360, 0);
        readonly Dictionary<string, RadioButton> think = new Dictionary<string, RadioButton>();
        Slider temp, topP, topK;
        TextBlock oThink, oTemp, oTopP, oTopK;
        TextBox max, seed;
        ToggleButton show, share;
        bool sharedOn, open;
        readonly System.Windows.Threading.DispatcherTimer closeTimer = new System.Windows.Threading.DispatcherTimer();

        public SamplingDrawer(MainWindow window)
        {
            w = window;
            // WPF may pause animation clocks while the window is hidden or minimized.
            closeTimer.Interval = TimeSpan.FromMilliseconds(220);
            closeTimer.Tick += delegate { closeTimer.Stop(); if (!open) Root.Visibility = Visibility.Collapsed; };
            Grid g = new Grid();
            g.Visibility = Visibility.Collapsed;
            scrim.SetResourceReference(Border.BackgroundProperty, "StScrim");
            scrim.MouseLeftButtonDown += delegate { Close(); };
            g.Children.Add(scrim);

            panel.Width = 360;
            panel.HorizontalAlignment = HorizontalAlignment.Right;
            panel.BorderThickness = new Thickness(1, 0, 0, 0);
            panel.SetResourceReference(Border.BackgroundProperty, "StSurface");
            panel.SetResourceReference(Border.BorderBrushProperty, "StLine");
            panel.RenderTransform = slide;
            panel.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, Opacity = 0.3, BlurRadius = 40, ShadowDepth = 10, Direction = 180 };

            Grid pg = new Grid();
            pg.RowDefinitions.Add(Ui.Row(Ui.Auto));
            pg.RowDefinitions.Add(Ui.Row(Ui.Star(1)));
            Grid head = new Grid { Margin = new Thickness(20, 14, 12, 14) };
            head.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            head.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            head.Children.Add(Ui.At(Ui.Text("Sampling", 16, "StInk", "bold"), 0, 0));
            Button close = Ui.IconButton("chevron", "Close", delegate { Close(); });
            ((IconView)close.Content).RenderTransformOrigin = new Point(0.5, 0.5);
            ((IconView)close.Content).RenderTransform = new RotateTransform(-90);
            head.Children.Add(Ui.At(close, 0, 1));
            Border rule = new Border { BorderThickness = new Thickness(0, 0, 0, 1), VerticalAlignment = VerticalAlignment.Bottom };
            rule.SetResourceReference(Border.BorderBrushProperty, "StLine");
            Grid hh = new Grid();
            hh.Children.Add(head); hh.Children.Add(rule);
            pg.Children.Add(Ui.At(hh, 0, 0));

            StackPanel body = new StackPanel { Margin = new Thickness(20) };
            body.Children.Add(Field("Thinking", out oThink, BuildSeg()));
            body.Children.Add(Gap());
            temp = MakeSlider(0, 1.5, 0.05);
            body.Children.Add(Field("Temperature", out oTemp, temp, "0 = always the most likely word (exact, repeatable)"));
            body.Children.Add(Gap());
            topP = MakeSlider(0.05, 1, 0.05);
            body.Children.Add(Field("Top-p", out oTopP, topP));
            body.Children.Add(Gap());
            topK = MakeSlider(1, 64, 1);
            body.Children.Add(Field("Top-k", out oTopK, topK));
            body.Children.Add(Gap());
            TextBlock o1;
            max = new TextBox { Style = Ui.Style("StInput") };
            body.Children.Add(Field("Max tokens", out o1, max));
            o1.Text = "empty = until done";
            body.Children.Add(Gap());
            TextBlock o2;
            seed = new TextBox { Style = Ui.Style("StInput") };
            body.Children.Add(Field("Seed", out o2, seed));
            o2.Text = "empty = random";
            body.Children.Add(Gap());
            show = Ui.Toggle(true, null);
            body.Children.Add(ToggleRow("Show thinking", "expanded while it streams", show));
            body.Children.Add(Gap());
            share = Ui.Toggle(false, null);
            body.Children.Add(ToggleRow("Use for other apps too", "API clients get these settings for anything they don't set themselves", share));
            body.Children.Add(Gap());
            Grid actions = new Grid();
            actions.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            actions.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            Button reset = Ui.Btn("secondary", "Reset", null, delegate { Load(new Settings()); });
            reset.Margin = new Thickness(0, 0, 6, 0);
            Button apply = Ui.Btn("primary", "Apply", null, delegate { Apply(); });
            apply.Margin = new Thickness(6, 0, 0, 0);
            actions.Children.Add(Ui.At(reset, 0, 0)); actions.Children.Add(Ui.At(apply, 0, 1));
            body.Children.Add(actions);
            ScrollViewer sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
            pg.Children.Add(Ui.At(sv, 1, 0));
            panel.Child = pg;
            g.Children.Add(panel);
            Root = g;
        }

        static FrameworkElement Gap() { return new Border { Height = 22 }; }

        FrameworkElement BuildSeg()
        {
            Border seg = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(4), BorderThickness = new Thickness(1) };
            seg.SetResourceReference(Border.BackgroundProperty, "StSurface2");
            seg.SetResourceReference(Border.BorderBrushProperty, "StLine");
            UniformGrid ug = new UniformGrid { Columns = 4 };
            string[][] items = new string[][] { new string[] { "none", "Off" }, new string[] { "low", "Low" }, new string[] { "medium", "Medium" }, new string[] { "high", "High" } };
            foreach (string[] it in items)
            {
                RadioButton r = new RadioButton();
                r.Style = Ui.Style("StSeg");
                r.GroupName = "think";
                r.Content = it[1];
                r.Margin = new Thickness(0, 0, 4, 0);
                string v = it[0];
                r.Checked += delegate { Outputs(); };
                think[v] = r;
                ug.Children.Add(r);
            }
            seg.Child = ug;
            return seg;
        }

        Slider MakeSlider(double min, double maxV, double step)
        {
            Slider s = new Slider();
            s.Style = Ui.Style("StSlider");
            s.Minimum = min; s.Maximum = maxV; s.SmallChange = step; s.LargeChange = step * 4; s.TickFrequency = step;
            s.IsSnapToTickEnabled = true;
            s.ValueChanged += delegate { Outputs(); };
            return s;
        }

        static FrameworkElement Field(string label, out TextBlock output, UIElement control) { return Field(label, out output, control, null); }

        static FrameworkElement Field(string label, out TextBlock output, UIElement control, string hint)
        {
            StackPanel sp = new StackPanel();
            Grid row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            row.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            row.Children.Add(Ui.At(Ui.Text(label, 13, "StInkSoft", "medium"), 0, 0));
            output = Ui.Text("", 13, "StInkMuted");
            output.HorizontalAlignment = HorizontalAlignment.Right;
            row.Children.Add(Ui.At(output, 0, 1));
            sp.Children.Add(row);
            sp.Children.Add(control);
            if (hint != null) { TextBlock h = Ui.Text(hint, 12, "StInkMuted", "regular", true); h.Margin = new Thickness(0, 6, 0, 0); sp.Children.Add(h); }
            return sp;
        }

        static FrameworkElement ToggleRow(string label, string sub, ToggleButton t)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            g.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            StackPanel sp = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            sp.Children.Add(Ui.Text(label, 13, "StInkSoft", "medium"));
            sp.Children.Add(Ui.Text(sub, 12, "StInkMuted", "regular", true));
            g.Children.Add(Ui.At(sp, 0, 0));
            t.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(Ui.At(t, 0, 1));
            return g;
        }

        void Outputs()
        {
            if (oTemp == null) return;
            oTemp.Text = temp.Value == 0 ? "0 \u00b7 greedy" : temp.Value.ToString("0.00");
            oTopP.Text = topP.Value.ToString("0.00");
            oTopK.Text = ((int)topK.Value).ToString();
            string sel = Selected();
            oThink.Text = sel == "none" ? "answers right away" : sel == "low" ? "short" : sel == "medium" ? "medium" : "thorough (default)";
            topP.IsEnabled = topK.IsEnabled = temp.Value > 0;
        }

        string Selected()
        {
            foreach (KeyValuePair<string, RadioButton> kv in think) if (kv.Value.IsChecked == true) return kv.Key;
            return "high";
        }

        void Load(Settings s)
        {
            string e = think.ContainsKey(s.Effort) ? s.Effort : "high";
            think[e].IsChecked = true;
            temp.Value = s.Temperature; topP.Value = s.TopP; topK.Value = s.TopK;
            max.Text = s.MaxTokens; seed.Text = s.Seed;
            show.IsChecked = s.ShowThinking;
            share.IsChecked = sharedOn;
            Outputs();
        }

        public void Open()
        {
            closeTimer.Stop();
            Load(w.Settings);
            LoadShared();
            Root.Visibility = Visibility.Visible;
            scrim.Visibility = Visibility.Visible;
            DoubleAnimation a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
            a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            slide.BeginAnimation(TranslateTransform.XProperty, a);
            open = true;
        }

        public void Close()
        {
            if (!open) return;
            open = false;
            DoubleAnimation a = new DoubleAnimation(360, TimeSpan.FromMilliseconds(180));
            a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn };
            a.Completed += delegate { if (!open) Root.Visibility = Visibility.Collapsed; };
            slide.BeginAnimation(TranslateTransform.XProperty, a);
            scrim.Visibility = Visibility.Collapsed;
            closeTimer.Start();
        }

        void Apply()
        {
            Settings s = w.Settings;
            s.Effort = Selected();
            s.Temperature = Math.Round(temp.Value, 2);
            s.TopP = Math.Round(topP.Value, 2);
            s.TopK = (int)topK.Value;
            s.MaxTokens = max.Text.Trim();
            s.Seed = seed.Text.Trim();
            s.ShowThinking = show.IsChecked == true;
            s.Save();
            bool wantShare = share.IsChecked == true;
            Close();
            if (wantShare || sharedOn) SaveShared(wantShare, s);
            else w.Toast("success", "Sampling saved", "Used by the next message in this chat.", 2500);
        }

        // "Use for other apps too": the server keeps these as every client's defaults (GET / POST /settings)
        HttpWebRequest Req(string method)
        {
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + w.Launcher.Port + "/settings");
            r.Method = method; r.Proxy = null; r.Timeout = 5000;
            if (!string.IsNullOrEmpty(w.Settings.ApiKey)) r.Headers["Authorization"] = "Bearer " + w.Settings.ApiKey;
            return r;
        }

        void LoadShared()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool on = false;
                try
                {
                    using (HttpWebResponse resp = (HttpWebResponse)Req("GET").GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                        on = J.Bool(new JavaScriptSerializer().DeserializeObject(sr.ReadToEnd()), "shared");
                }
                catch { }
                w.Dispatcher.BeginInvoke(new Action(delegate { sharedOn = on; share.IsChecked = on; }));
            });
        }

        void SaveShared(bool on, Settings s)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                bool result = false;
                try
                {
                    Dictionary<string, object> defaults = null;
                    if (on)
                    {
                        defaults = new Dictionary<string, object>();
                        defaults["reasoning_effort"] = s.Effort;
                        defaults["temperature"] = s.Temperature;
                        if (s.Temperature > 0) { defaults["top_p"] = s.TopP; defaults["top_k"] = s.TopK; }
                        int n;
                        if (s.Seed.Length > 0 && int.TryParse(s.Seed, out n)) defaults["seed"] = n;
                        if (s.MaxTokens.Length > 0 && int.TryParse(s.MaxTokens, out n)) defaults["max_tokens"] = n;
                    }
                    Dictionary<string, object> body = new Dictionary<string, object>();
                    body["defaults"] = defaults;
                    byte[] payload = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));
                    HttpWebRequest r = Req("POST");
                    r.ContentType = "application/json";
                    r.ContentLength = payload.Length;
                    using (Stream rs = r.GetRequestStream()) rs.Write(payload, 0, payload.Length);
                    using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                        result = J.Bool(new JavaScriptSerializer().DeserializeObject(sr.ReadToEnd()), "shared");
                }
                catch (Exception ex) { error = ex.Message; }
                w.Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (error != null) w.Toast("error", "Saved here, but not for other apps", error, 6000);
                    else { sharedOn = result; w.Toast("success", "Sampling saved", result ? "Other apps use these settings from their next request." : "Other apps use their own settings again.", 4000); }
                }));
            });
        }
    }
}
