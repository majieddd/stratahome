using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace StrataHome
{
    /// <summary>The About tab: model and engine facts, this PC, the API addresses, settings and the model's run-config form.</summary>
    internal sealed class AboutView
    {
        public readonly FrameworkElement Root;
        readonly MainWindow w;
        StackPanel engineHost, hwHost, apiHost;
        ToggleButton darkToggle;
        PasswordBox key;
        Border cfgCard, ctxCard;
        WrapPanel ctxChips;
        TextBlock ctxNow, ctxHint, ctxMsg;
        string ctxSig = null;
        StackPanel cfgForm;
        TextBlock cfgFile, cfgMsg;
        string sig = "";
        List<Dictionary<string, object>> cfgKeys = new List<Dictionary<string, object>>();
        readonly List<Func<object>> cfgReaders = new List<Func<object>>();
        bool cfgLoaded;

        public AboutView(MainWindow window)
        {
            w = window;
            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            StackPanel col = new StackPanel();
            col.MaxWidth = 860;
            col.Margin = new Thickness(24);

            engineHost = new StackPanel(); hwHost = new StackPanel(); apiHost = new StackPanel();
            col.Children.Add(Section("Model and engine", engineHost, null));
            col.Children.Add(Section("This PC", hwHost, null));
            col.Children.Add(Section("Connect your tools", apiHost, "Any OpenAI- or Anthropic-compatible client works with these addresses."));
            col.Children.Add(BuildSettings());
            col.Children.Add(BuildContextCard());
            col.Children.Add(BuildConfigCard());
            sv.Content = col;
            Root = sv;
            RefreshStatic();
        }

        static FrameworkElement Section(string title, StackPanel host, string note)
        {
            StackPanel sp = new StackPanel();
            sp.Children.Add(Ui.Text(title, 16, "StInk", "bold"));
            if (note != null) { TextBlock n = Ui.Text(note, 14, "StInkMuted", "regular", true); n.Margin = new Thickness(0, 12, 0, 0); sp.Children.Add(n); }
            host.Margin = new Thickness(0, 12, 0, 0);
            sp.Children.Add(host);
            Border b = Ui.Card(sp);
            b.Margin = new Thickness(0, 0, 0, 20);
            return b;
        }

        FrameworkElement BuildSettings()
        {
            StackPanel sp = new StackPanel();
            sp.Children.Add(Ui.Text("Settings", 16, "StInk", "bold"));
            Grid lab = new Grid { Margin = new Thickness(0, 16, 0, 8) };
            lab.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            lab.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            lab.Children.Add(Ui.At(Ui.Text("API key", 13, "StInkSoft", "medium"), 0, 0));
            TextBlock o = Ui.Text("only if the server was started with one", 13, "StInkMuted");
            o.HorizontalAlignment = HorizontalAlignment.Right;
            lab.Children.Add(Ui.At(o, 0, 1));
            sp.Children.Add(lab);
            key = new PasswordBox();
            key.Style = Ui.Style("StPasswordBox");
            key.Password = w.Settings.ApiKey;
            key.LostKeyboardFocus += delegate
            {
                string k = key.Password.Trim();
                if (k == w.Settings.ApiKey) return;
                w.Settings.ApiKey = k;
                w.Settings.Save();
                w.Toast("success", "API key saved", "Kept on this PC only.", 3500);
            };
            sp.Children.Add(key);

            Grid row = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            row.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            row.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            row.Children.Add(Ui.At(Ui.Text("Dark theme", 13, "StInkSoft", "medium"), 0, 0));
            darkToggle = Ui.Toggle(Theme.Dark, delegate(bool on) { w.SetTheme(on, true); });
            row.Children.Add(Ui.At(darkToggle, 0, 1));
            sp.Children.Add(row);

            TextBlock note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0), FontSize = 12, FontFamily = Fonts.Regular };
            note.SetResourceReference(TextBlock.ForegroundProperty, "StInkMuted");
            note.Inlines.Add(new Run("Chats, settings and the key are kept on this PC only. "));
            Hyperlink h = new Hyperlink(new Run("Strata on GitHub"));
            h.NavigateUri = new Uri("https://github.com/Niko1221/Strata");
            h.SetResourceReference(TextElement.ForegroundProperty, "StAccentText");
            h.Click += delegate { try { Process.Start("https://github.com/Niko1221/Strata"); } catch { } };
            note.Inlines.Add(h);
            sp.Children.Add(note);
            TextBlock credit = Ui.Text("StrataHome " + typeof(AboutView).Assembly.GetName().Version.ToString(3) + " \u00b7 unofficial \u00b7 MIT. This window follows the look of Strata's own web app.", 12, "StInkMuted", "regular", true);
            credit.Margin = new Thickness(0, 8, 0, 0);
            sp.Children.Add(credit);
            Border b = Ui.Card(sp);
            b.Margin = new Thickness(0, 0, 0, 20);
            return b;
        }

        // ------------------------------------------------------------------ context length (--max-context in the run config)

        FrameworkElement BuildContextCard()
        {
            StackPanel sp = new StackPanel();
            Grid head = new Grid();
            head.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            head.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            head.Children.Add(Ui.At(Ui.Text("Context length", 16, "StInk", "bold"), 0, 0));
            ctxNow = Ui.Text("", 12, "StInkMuted");
            head.Children.Add(Ui.At(ctxNow, 0, 1));
            sp.Children.Add(head);
            TextBlock n = Ui.Text("How much one conversation can hold, in tokens. A longer context keeps more of a long chat or a big file in view, and uses more memory. The model was trained for up to 256K. It is used from the next start of the model.", 12, "StInkMuted", "regular", true);
            n.Margin = new Thickness(0, 12, 0, 12);
            sp.Children.Add(n);
            Border seg = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(4), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left };
            seg.SetResourceReference(Border.BackgroundProperty, "StSurface2");
            seg.SetResourceReference(Border.BorderBrushProperty, "StLine");
            ctxChips = new WrapPanel();
            seg.Child = ctxChips;
            sp.Children.Add(seg);
            ctxHint = Ui.Text("", 12, "StInkMuted", "regular", true);
            ctxHint.Margin = new Thickness(0, 10, 0, 0);
            sp.Children.Add(ctxHint);
            StackPanel act = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            Button restart = Ui.Btn("primary", "Save and restart", null, delegate { ApplyContext(true); });
            Button save = Ui.Btn("secondary", "Save for next start", null, delegate { ApplyContext(false); });
            save.Margin = new Thickness(10, 0, 0, 0);
            act.Children.Add(restart);
            act.Children.Add(save);
            sp.Children.Add(act);
            ctxMsg = Ui.Text("", 12, "StInkMuted", "regular", true);
            ctxMsg.Margin = new Thickness(0, 10, 0, 0);
            ctxMsg.Visibility = Visibility.Collapsed;
            sp.Children.Add(ctxMsg);
            ctxCard = Ui.Card(sp);
            ctxCard.Margin = new Thickness(0, 0, 0, 20);
            ctxCard.Visibility = Visibility.Collapsed;
            return ctxCard;
        }

        int PickedContext()
        {
            foreach (object c in ctxChips.Children)
            {
                RadioButton rb = c as RadioButton;
                if (rb != null && rb.IsChecked == true) return (int)rb.Tag;
            }
            return 0;
        }

        /// <summary>Shows the selected model's current --max-context and the sizes to pick; rebuilt only when that changes.</summary>
        void RefreshContext()
        {
            ModelEntry m = w.SelectedModel;
            string s = m == null ? "" : m.FileName + "|" + m.MaxContext;
            if (s == ctxSig) return;
            ctxSig = s;
            ctxChips.Children.Clear();
            if (m == null || m.MaxContext <= 0) { ctxCard.Visibility = Visibility.Collapsed; return; }
            ctxCard.Visibility = Visibility.Visible;
            List<int> sizes = new List<int>(RunConfig.Presets);
            if (!sizes.Contains(m.MaxContext)) { sizes.Add(m.MaxContext); sizes.Sort(); }
            foreach (int size in sizes)
            {
                RadioButton rb = new RadioButton { Style = Ui.Style("StSeg"), Content = StrataInstall.Tokens(size), GroupName = "ctx", Tag = size, MinWidth = 64, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(10, 0, 10, 0) };
                rb.IsChecked = size == m.MaxContext;
                rb.Checked += delegate { UpdateContextHint(); };
                ctxChips.Children.Add(rb);
            }
            ctxNow.Text = "now " + StrataInstall.Tokens(m.MaxContext) + " tokens";
            UpdateContextHint();
        }

        void UpdateContextHint()
        {
            ModelEntry m = w.SelectedModel;
            int pick = PickedContext();
            if (m == null || pick <= 0) { ctxHint.Text = ""; return; }
            double kb = RunConfig.KvKbPerToken(RunConfig.ArgValue(m.ConfigPath, "--kv"));
            string t = pick.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " tokens";
            if (kb > 0) t += ", about " + Fmt.N(pick * kb / 1000000.0, 1) + " GB for the KV cache (" + (pick >= RunConfig.StreamFrom ? "mostly in RAM" : "in VRAM") + ")";
            if (pick > 131072) t += ". Long contexts need more RAM; if the model fails to load, pick a smaller size";
            if (pick != m.MaxContext) t += ". Changes from " + StrataInstall.Tokens(m.MaxContext);
            ctxHint.Text = t + ".";
        }

        void SayContext(string text) { ctxMsg.Text = text; ctxMsg.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }

        void ApplyContext(bool restart)
        {
            if (w.Updater.Busy) return;
            ModelEntry m = w.SelectedModel;
            int pick = PickedContext();
            if (m == null || pick <= 0) return;
            if (pick == m.MaxContext) { SayContext("Already " + StrataInstall.Tokens(pick) + ": nothing to change."); return; }
            string err = RunConfig.SetContext(m.ConfigPath, pick, Native.TotalRamGb());
            if (err != null) { SayContext(""); w.Toast("error", "Not saved", err, 6000); return; }
            w.LoadInstall();
            w.Server.RefreshAll();
            ctxSig = null;
            RefreshContext();
            SayContext("Saved " + StrataInstall.Tokens(pick) + ". The earlier file is kept as " + m.FileName + ".bak.");
            if (restart) { w.Toast("info", "Restarting Strata", "Using a " + StrataInstall.Tokens(pick) + " context.", 4000); w.RestartServer(); }
            else if (w.Launcher.IsActive) w.Toast("success", "Context saved", StrataInstall.Tokens(pick) + " is used from the next start.", 9000, "Restart now", delegate { w.RestartServer(); });
        }

        FrameworkElement BuildConfigCard()
        {
            StackPanel sp = new StackPanel();
            Grid head = new Grid();
            head.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            head.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            head.Children.Add(Ui.At(Ui.Text("Model settings", 16, "StInk", "bold"), 0, 0));
            cfgFile = Ui.Text("", 12, "StInkMuted");
            head.Children.Add(Ui.At(cfgFile, 0, 1));
            sp.Children.Add(head);
            TextBlock n = Ui.Text("Kept in the model's run config for every client. An empty field is the default. They take effect the next time the model starts.", 12, "StInkMuted", "regular", true);
            n.Margin = new Thickness(0, 12, 0, 12);
            sp.Children.Add(n);
            cfgForm = new StackPanel();
            sp.Children.Add(cfgForm);
            Grid act = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            act.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            act.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            Button save = Ui.Btn("primary", "Save", null, delegate { SaveConfig(); });
            act.Children.Add(Ui.At(save, 0, 0));
            cfgMsg = Ui.Text("", 12, "StInkMuted", "regular", true);
            cfgMsg.Margin = new Thickness(12, 0, 0, 0);
            cfgMsg.VerticalAlignment = VerticalAlignment.Center;
            act.Children.Add(Ui.At(cfgMsg, 0, 1));
            sp.Children.Add(act);
            cfgCard = Ui.Card(sp);
            cfgCard.Visibility = Visibility.Collapsed;
            return cfgCard;
        }

        public void RefreshTheme() { if (darkToggle != null) darkToggle.IsChecked = Theme.Dark; }

        void RefreshStatic()
        {
            Fill(apiHost, new List<string[]>
            {
                new string[] { "OpenAI base URL", w.Launcher.ApiUrl, "copy" },
                new string[] { "Anthropic base URL", "http://127.0.0.1:" + w.Launcher.Port, "copy" },
                new string[] { "Model name", w.Launcher.ServedModel.Length > 0 ? w.Launcher.ServedModel : (w.SelectedModel != null ? w.SelectedModel.ModelName : ""), "copy" }
            });
        }

        static void Fill(StackPanel host, List<string[]> rows)
        {
            host.Children.Clear();
            host.Children.Add(Ui.Facts(rows));
        }

        public void Render(Dictionary<string, object> m)
        {
            RefreshStatic();
            RefreshContext();
            if (!cfgLoaded && w.Launcher.IsActive) LoadConfig();
            if (m == null) return;
            object eng = J.Get(m, "engine"), hw = J.Get(m, "hardware"), st = J.Get(m, "hardware_static");
            string kvName = J.Str(eng, "kv");
            string kv = kvName == "int8" ? "8-bit" : kvName == "q4_0" ? "4-bit (Hadamard-rotated)" : kvName == "fp16" ? "16-bit" : kvName;
            double? resident = J.Dbl(eng, "kv_resident"), slots = J.Dbl(eng, "expert_slots"), spec = J.Dbl(eng, "spec"), mtp = J.Dbl(eng, "mtp_max");
            string cvec = J.Str(eng, "cvec");
            List<string[]> engine = new List<string[]>();
            engine.Add(new string[] { "Model", J.Str(eng, "model") });
            engine.Add(new string[] { "Engine", J.Str(eng, "version") != null ? "v" + J.Str(eng, "version") : "built from source" });
            double? ctx = J.Dbl(eng, "max_context");
            engine.Add(new string[] { "Context", ctx > 0 ? Fmt.N(ctx) + " tokens" : null });
            engine.Add(new string[] { "KV cache", kv == null ? null : kv + (resident > 0 ? ", streamed: " + Fmt.N(resident) + " positions per layer in VRAM, the rest in RAM" : ", all in VRAM") });
            engine.Add(new string[] { "Experts in VRAM", slots > 0 ? Fmt.N(slots) + " (" + Fmt.GB((J.Dbl(eng, "expert_cache_mib") ?? 0) * 1048576) + " GB)" : null });
            engine.Add(new string[] { "Speculation", spec > 0 ? "MTP drafts up to " + Math.Max(0, (int)((mtp ?? spec) - 1)) + " tokens" + (J.Dbl(eng, "lookup") > 0 ? ", prompt lookup on" : "") : null });
            engine.Add(new string[] { "Images", J.Bool(eng, "images") ? "on" : "off" });
            if (!string.IsNullOrEmpty(cvec) && cvec != "0") engine.Add(new string[] { "Experimental speed projection", "Control vector " + cvec + ". Per chat in Sampling." });
            double? memTotal = J.Dbl(hw, "gpu_mem_total");
            List<string[]> pc = new List<string[]>();
            string gpuName = J.Str(st, "gpu_name");
            pc.Add(new string[] { "GPU", gpuName != null ? gpuName + (memTotal > 0 ? ", " + Fmt.GB(memTotal, 0) + " GB" : "") : "not readable (NVML)" });
            string cpuName = J.Str(st, "cpu_name");
            pc.Add(new string[] { "CPU", cpuName != null ? cpuName + (J.Dbl(st, "threads") > 0 ? ", " + Fmt.N(J.Dbl(st, "threads")) + " threads" : "") : null });
            pc.Add(new string[] { "RAM", J.Dbl(hw, "ram_total") > 0 ? Fmt.GB(J.Dbl(hw, "ram_total"), 0) + " GB" : null });

            StringBuilder s = new StringBuilder();
            foreach (string[] r in engine) s.Append(r[0]).Append('=').Append(r[1]).Append(';');
            foreach (string[] r in pc) s.Append(r[0]).Append('=').Append(r[1]).Append(';');
            if (s.ToString() == sig) return;                          // nothing changed: do not rebuild the lists every second
            sig = s.ToString();
            Fill(engineHost, engine);
            Fill(hwHost, pc);
        }

        // ------------------------------------------------------------------ the run config (GET / POST /config)

        HttpWebRequest Req(string method)
        {
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + w.Launcher.Port + "/config");
            r.Method = method; r.Proxy = null; r.Timeout = 5000;
            if (!string.IsNullOrEmpty(w.Settings.ApiKey)) r.Headers["Authorization"] = "Bearer " + w.Settings.ApiKey;
            return r;
        }

        void LoadConfig()
        {
            cfgLoaded = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    using (HttpWebResponse resp = (HttpWebResponse)Req("GET").GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                    {
                        object c = new JavaScriptSerializer().DeserializeObject(sr.ReadToEnd());
                        w.Dispatcher.BeginInvoke(new Action(delegate { BuildConfigForm(c); }));
                    }
                }
                catch { w.Dispatcher.BeginInvoke(new Action(delegate { cfgCard.Visibility = Visibility.Collapsed; cfgLoaded = false; })); }
            });
        }

        void BuildConfigForm(object c)
        {
            cfgKeys.Clear();
            cfgReaders.Clear();
            cfgForm.Children.Clear();
            cfgFile.Text = J.Str(c, "file") ?? "";
            foreach (object k in J.List(c, "keys"))
            {
                Dictionary<string, object> key = k as Dictionary<string, object>;
                if (key == null) continue;
                cfgKeys.Add(key);
                string kind = J.Str(key, "kind");
                object v = J.Get(key, "value");
                Grid row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
                row.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
                row.ColumnDefinitions.Add(Ui.Col(new GridLength(300)));
                StackPanel lab = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
                lab.Children.Add(Ui.Text(J.Str(key, "help") ?? "", 13, "StInkSoft", "medium", true));
                lab.Children.Add(Ui.Mono(J.Str(key, "key") ?? "", 12, "StInkMuted"));
                row.Children.Add(Ui.At(lab, 0, 0));
                if (kind == "bool" || kind == "enum")
                {
                    List<string[]> opts = new List<string[]>();
                    opts.Add(new string[] { "", "default" });
                    if (kind == "bool") { opts.Add(new string[] { "true", "on" }); opts.Add(new string[] { "false", "off" }); }
                    else foreach (object ch in J.List(key, "choices")) opts.Add(new string[] { Convert.ToString(ch), Convert.ToString(ch) });
                    string cur = v == null ? "" : (v is bool ? ((bool)v ? "true" : "false") : Convert.ToString(v));
                    Border seg = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(4), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left };
                    seg.SetResourceReference(Border.BackgroundProperty, "StSurface2");
                    seg.SetResourceReference(Border.BorderBrushProperty, "StLine");
                    StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal };
                    string group = "cfg" + cfgKeys.Count;
                    List<RadioButton> rbs = new List<RadioButton>();
                    foreach (string[] o in opts)
                    {
                        RadioButton rb = new RadioButton { Style = Ui.Style("StSeg"), Content = o[1], GroupName = group, Tag = o[0], MinWidth = 64, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(10, 0, 10, 0) };
                        rb.IsChecked = o[0] == cur;
                        rbs.Add(rb);
                        sp.Children.Add(rb);
                    }
                    seg.Child = sp;
                    row.Children.Add(Ui.At(seg, 0, 1));
                    cfgReaders.Add(delegate
                    {
                        foreach (RadioButton rb in rbs) if (rb.IsChecked == true) { string t = (string)rb.Tag; return t == "" ? null : kind == "bool" ? (object)(t == "true") : t; }
                        return null;
                    });
                }
                else
                {
                    string text = v == null ? "" : v is IList ? string.Join(", ", ToStrings((IList)v)) : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
                    TextBox tb = new TextBox { Style = Ui.Style("StInput"), Height = 38, Text = text };
                    row.Children.Add(Ui.At(tb, 0, 1));
                    cfgReaders.Add(delegate
                    {
                        string s = tb.Text.Trim();
                        if (s.Length == 0) return null;
                        if (kind == "number") { double d; return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? (object)d : s; }
                        return s;
                    });
                }
                cfgForm.Children.Add(row);
            }
            cfgCard.Visibility = cfgKeys.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        static string[] ToStrings(IList l)
        {
            List<string> r = new List<string>();
            foreach (object o in l) r.Add(Convert.ToString(o));
            return r.ToArray();
        }

        public int ConfigFieldCount { get { return cfgKeys.Count; } }

        public void ChooseContextForTest(int ctx, bool restart)
        {
            foreach (object c in ctxChips.Children)
            {
                RadioButton rb = c as RadioButton;
                if (rb != null && (int)rb.Tag == ctx) rb.IsChecked = true;
            }
            ApplyContext(restart);
        }

        public void ScrollTo(double y) { ScrollViewer sv = Root as ScrollViewer; if (sv != null) sv.ScrollToVerticalOffset(y); }

        // for --uitest
        public int ContextChipCount { get { return ctxChips.Children.Count; } }
        public int ContextPicked { get { return PickedContext(); } }
        public bool ContextCardVisible { get { return ctxCard.Visibility == Visibility.Visible; } }

        void SaveConfig()
        {
            Dictionary<string, object> set = new Dictionary<string, object>();
            for (int i = 0; i < cfgKeys.Count; i++)
            {
                object nv = cfgReaders[i]();
                object old = J.Get(cfgKeys[i], "value");
                string oldS = old == null ? null : old is IList ? string.Join(", ", ToStrings((IList)old)) : old is bool ? ((bool)old ? "true" : "false") : Convert.ToString(old, System.Globalization.CultureInfo.InvariantCulture);
                string newS = nv == null ? null : nv is bool ? ((bool)nv ? "true" : "false") : Convert.ToString(nv, System.Globalization.CultureInfo.InvariantCulture);
                if (oldS != newS) set[J.Str(cfgKeys[i], "key")] = nv;
            }
            if (set.Count == 0) { cfgMsg.Text = "Nothing changed."; return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                string msg = null, err = null;
                try
                {
                    Dictionary<string, object> body = new Dictionary<string, object>();
                    body["set"] = set;
                    byte[] payload = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));
                    HttpWebRequest r = Req("POST");
                    r.ContentType = "application/json";
                    r.ContentLength = payload.Length;
                    using (Stream rs = r.GetRequestStream()) rs.Write(payload, 0, payload.Length);
                    using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                    {
                        object b = new JavaScriptSerializer().DeserializeObject(sr.ReadToEnd());
                        IList changed = J.List(b, "changed");
                        msg = changed.Count > 0 ? "Saved (" + string.Join(", ", ToStrings(changed)) + "); the earlier file is " + J.Str(b, "file") + ".bak. Restart Strata to use it." : "Nothing changed.";
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                w.Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (err != null) { cfgMsg.Text = ""; w.Toast("error", "Not saved", err, 6000); }
                    else { cfgMsg.Text = msg; cfgLoaded = false; LoadConfig(); }
                }));
            });
        }
    }
}
