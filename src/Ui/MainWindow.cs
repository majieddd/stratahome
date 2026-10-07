using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace StrataHome
{
    /// <summary>What was asked on the command line.</summary>
    internal sealed class Options
    {
        public bool Minimized, NoStart, Drawer, UiTest;
        public double WidthPx, HeightPx;
        public string Mode, ScreenshotPath, Tab, ThemeName, Prompt;
        public int IdleMinutes;
        public double DelaySeconds = 3;
    }

    /// <summary>
    /// The StrataHome window, in the look of the Strata web app: header with tabs and a status pill, then one page per tab
    /// (Chat, Monitor, About) plus Server, which holds the launcher's own controls.
    /// </summary>
    internal sealed class MainWindow : Window
    {
        public readonly Launcher Launcher = new Launcher();
        public readonly Settings Settings = Settings.Load();
        public readonly MetricsPoller Metrics = new MetricsPoller();
        public readonly Options Opt;
        public List<ModelEntry> Models = new List<ModelEntry>();
        public ModelEntry SelectedModel;

        public ChatView Chat;
        public MonitorView Monitor;
        public AboutView About;
        public ServerView Server;
        public SamplingDrawer Drawer;

        readonly ConcurrentQueue<string> pendingLog = new ConcurrentQueue<string>();
        readonly Dictionary<string, RadioButton> tabs = new Dictionary<string, RadioButton>();
        readonly Dictionary<string, FrameworkElement> pages = new Dictionary<string, FrameworkElement>();
        readonly StackPanel toasts = new StackPanel();
        Tray tray;
        Border pill;
        Ellipse pillDot, pillHalo;
        TextBlock pillText;
        Button themeBtn;
        string tab = "chat";
        bool exiting, toldAboutTray, shotScheduled;

        public MainWindow(Options opt)
        {
            Opt = opt;
            if (opt.Mode == "always" || opt.Mode == "idle" || opt.Mode == "ondemand") Settings.Mode = opt.Mode;
            if (opt.IdleMinutes > 0) Settings.IdleMinutes = Math.Min(1440, opt.IdleMinutes);
            Paths.Diag("settings in effect: mode " + Settings.Mode + ", idle " + Settings.IdleMinutes + " min, auto-start " + Settings.AutoStartServer);

            Title = "StrataHome";
            Width = 1240; Height = 820; MinWidth = 760; MinHeight = 560;
            if (opt.WidthPx > 0) Width = opt.WidthPx;
            if (opt.HeightPx > 0) Height = opt.HeightPx;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            UseLayoutRounding = true;
            SetResourceReference(BackgroundProperty, "StBg");
            SetResourceReference(Control.ForegroundProperty, "StInk");
            SetResourceReference(Control.FontFamilyProperty, "StFont");
            FontSize = 16;
            try
            {
                System.Drawing.Icon ico = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath);
                Icon = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            catch { }

            LoadInstall();

            Grid root = new Grid();
            root.RowDefinitions.Add(Ui.Row(Ui.Px(60)));
            root.RowDefinitions.Add(Ui.Row(Ui.Star(1)));

            root.Children.Add(Ui.At(BuildHeader(), 0, 0));

            Grid host = new Grid();
            Chat = new ChatView(this); Monitor = new MonitorView(this); About = new AboutView(this); Server = new ServerView(this);
            Add(host, "chat", Chat.Root); Add(host, "monitor", Monitor.Root); Add(host, "about", About.Root); Add(host, "server", Server.Root);
            root.Children.Add(Ui.At(host, 1, 0));

            // overlay: the sampling drawer (over everything, header included) and the toasts
            Grid overlay = new Grid();
            Drawer = new SamplingDrawer(this);
            overlay.Children.Add(Drawer.Root);
            toasts.HorizontalAlignment = HorizontalAlignment.Right;
            toasts.VerticalAlignment = VerticalAlignment.Bottom;
            toasts.Margin = new Thickness(0, 0, 20, 20);
            overlay.Children.Add(toasts);
            Grid.SetRowSpan(overlay, 2);
            Panel.SetZIndex(overlay, 10);
            overlay.IsHitTestVisible = true;
            root.Children.Add(overlay);

            Content = root;
            ShowTab(Opt.Tab ?? "chat");

            Launcher.Changed += delegate { Dispatcher.BeginInvoke(new Action(OnLauncherChanged)); };
            Launcher.LogLine += delegate(string l) { pendingLog.Enqueue(l); };
            Metrics.Updated += OnMetrics;
            Metrics.Lost += delegate { UpdatePill(); };
            Metrics.Start(delegate { return Launcher.Port; }, delegate { return Settings.ApiKey; },
                          delegate { return Launcher.IsActive && Launcher.State != RunState.Stopping; });

            DispatcherTimer tick = new DispatcherTimer();
            tick.Interval = TimeSpan.FromMilliseconds(400);
            tick.Tick += delegate { Tick(); };
            tick.Start();

            tray = new Tray(this);
            OnLauncherChanged();

            Closing += OnClosing;
            Metrics.Slow = Opt.Minimized;
            if (Opt.Minimized) TrimSoon();
            IsVisibleChanged += delegate { Metrics.Slow = !IsVisible; if (!IsVisible) TrimSoon(); };
            if (Opt.UiTest)
            {
                // run the UI test once the server answers (or after 20 s, then the live-server tests are skipped)
                DateTime t0 = DateTime.UtcNow;
                DispatcherTimer wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                wait.Tick += delegate
                {
                    bool up = Launcher.State == RunState.Ready || Launcher.State == RunState.External || Launcher.State == RunState.Unloaded;
                    if (up || (DateTime.UtcNow - t0).TotalSeconds > 20) { wait.Stop(); new UiTest(this).Start(); }
                };
                wait.Start();
            }
            SourceInitialized += delegate { Theme.TitleBar(this); };
            if (Settings.AutoStartServer && !Opt.NoStart && Models.Count > 0) StartServer();
        }

        void Add(Grid host, string name, FrameworkElement page)
        {
            page.Visibility = Visibility.Collapsed;
            pages[name] = page;
            host.Children.Add(page);
        }

        // ------------------------------------------------------------------ header

        FrameworkElement BuildHeader()
        {
            Border bar = new Border();
            bar.SetResourceReference(Border.BackgroundProperty, "StSurface");
            bar.SetResourceReference(Border.BorderBrushProperty, "StLine");
            bar.BorderThickness = new Thickness(0, 0, 0, 1);
            Grid g = new Grid();
            g.Margin = new Thickness(24, 0, 24, 0);
            g.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            g.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            g.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            g.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            g.ColumnDefinitions.Add(Ui.Col(Ui.Auto));

            StackPanel brand = new StackPanel();
            brand.Orientation = Orientation.Horizontal;
            brand.VerticalAlignment = VerticalAlignment.Center;
            TextBlock s = Ui.Text("Strata", 18, "StInk", "black");
            brand.Children.Add(s);
            TextBlock h = Ui.Text("Home", 18, "StInkMuted", "medium");
            h.Margin = new Thickness(3, 0, 0, 0);
            brand.Children.Add(h);
            g.Children.Add(Ui.At(brand, 0, 0));

            Border seg = new Border();
            seg.SetResourceReference(Border.BackgroundProperty, "StSurface2");
            seg.SetResourceReference(Border.BorderBrushProperty, "StLine");
            seg.BorderThickness = new Thickness(1);
            seg.CornerRadius = new CornerRadius(14);
            seg.Padding = new Thickness(4);
            seg.Margin = new Thickness(16, 0, 0, 0);
            seg.VerticalAlignment = VerticalAlignment.Center;
            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.Children.Add(MakeTab("chat", "Chat", "chat"));
            row.Children.Add(MakeTab("monitor", "Monitor", "activity"));
            row.Children.Add(MakeTab("about", "About", "info"));
            row.Children.Add(MakeTab("server", "Server", "bolt"));
            seg.Child = row;
            g.Children.Add(Ui.At(seg, 0, 1));

            pill = new Border();
            pill.Height = 30;
            pill.CornerRadius = new CornerRadius(15);
            pill.Padding = new Thickness(12, 0, 12, 0);
            pill.BorderThickness = new Thickness(1);
            pill.SetResourceReference(Border.BackgroundProperty, "StSurface2");
            pill.SetResourceReference(Border.BorderBrushProperty, "StLine");
            pill.VerticalAlignment = VerticalAlignment.Center;
            pill.Margin = new Thickness(0, 0, 8, 0);
            StackPanel ps = new StackPanel();
            ps.Orientation = Orientation.Horizontal;
            Grid dotBox = new Grid { Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            pillHalo = new Ellipse { Width = 16, Height = 16, Visibility = Visibility.Collapsed };
            pillDot = new Ellipse { Width = 8, Height = 8 };
            dotBox.Children.Add(pillHalo); dotBox.Children.Add(pillDot);
            ps.Children.Add(dotBox);
            pillText = Ui.Text("Connecting\u2026", 13, "StInkSoft", "medium");
            pillText.VerticalAlignment = VerticalAlignment.Center;
            ps.Children.Add(pillText);
            pill.Child = ps;
            g.Children.Add(Ui.At(pill, 0, 3));

            themeBtn = Ui.IconButton("moon", "Light / dark", delegate { SetTheme(!Theme.Dark, true); });
            themeBtn.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(Ui.At(themeBtn, 0, 4));
            bar.Child = g;
            return bar;
        }

        RadioButton MakeTab(string key, string label, string icon)
        {
            RadioButton b = new RadioButton();
            b.Style = Ui.Style("StTab");
            b.GroupName = "tabs";
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            IconView iv = new IconView(icon, 16);
            iv.VerticalAlignment = VerticalAlignment.Center;
            iv.Margin = new Thickness(0, 0, 8, 0);
            sp.Children.Add(iv);
            TextBlock t = new TextBlock();
            t.Text = label;
            t.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(t);
            b.Content = sp;
            b.Checked += delegate { ShowTab(key); };
            b.Margin = new Thickness(0, 0, 4, 0);
            tabs[key] = b;
            return b;
        }

        public void ShowTab(string name)
        {
            if (!pages.ContainsKey(name)) name = "chat";
            tab = name;
            foreach (KeyValuePair<string, FrameworkElement> kv in pages)
                kv.Value.Visibility = kv.Key == name ? Visibility.Visible : Visibility.Collapsed;
            if (!tabs[name].IsChecked.GetValueOrDefault()) tabs[name].IsChecked = true;
            if (name == "monitor") Monitor.Render(Metrics.Last);
            if (name == "about") About.Render(Metrics.Last);
            if (name == "chat") Chat.FocusInput();
            if (name == "server") Server.RefreshAll();
        }

        public string CurrentTab { get { return tab; } }

        // ------------------------------------------------------------------ the status pill

        void SetPill(string state, string text)
        {
            string dot = "StInkMuted";
            bool halo = false;
            string haloKey = null;
            switch (state)
            {
                case "generating": dot = "StAccent"; halo = true; haloKey = "StAccentTint"; break;
                case "reading": dot = "StInfo"; halo = true; haloKey = "StInfoTint"; break;
                case "queued": dot = "StWarn"; break;
                case "error": dot = "StDanger"; break;
            }
            pillDot.SetResourceReference(Shape.FillProperty, dot);
            pillHalo.Visibility = halo ? Visibility.Visible : Visibility.Collapsed;
            if (halo) pillHalo.SetResourceReference(Shape.FillProperty, haloKey);
            pillText.Text = text;
        }

        public void UpdatePill()
        {
            RunState s = Launcher.State;
            if (s == RunState.Ready || s == RunState.External)
            {
                object live = J.Get(Metrics.Last, "live");
                string st = J.Str(live, "state");
                double? queued = J.Dbl(live, "queued");
                if (queued > 0) { SetPill("queued", Fmt.N(queued) + " queued"); return; }
                if (st == "reading")
                {
                    double? total = J.Dbl(live, "prompt_total"), read = J.Dbl(live, "prompt_read");
                    SetPill("reading", total > 0 ? "Reading prompt \u00b7 " + Math.Round(100 * (read ?? 0) / total.Value) + "%" : "Reading prompt");
                    return;
                }
                if (st == "generating") { SetPill("generating", "Generating \u00b7 " + Fmt.N(J.Dbl(live, "tok_s"), 1) + " tok/s"); return; }
                SetPill("idle", "Idle");
                return;
            }
            SetPill(s == RunState.Error ? "error" : s == RunState.Starting || s == RunState.Loading || s == RunState.Stopping ? "queued" : "idle",
                    StateTitle(s));
        }

        public static string StateTitle(RunState s)
        {
            switch (s)
            {
                case RunState.Stopped: return "Stopped";
                case RunState.Starting: return "Starting";
                case RunState.Loading: return "Loading the model";
                case RunState.Ready: return "Ready";
                case RunState.Unloaded: return "Idle \u00b7 model unloaded";
                case RunState.External: return "Ready";
                case RunState.Stopping: return "Stopping";
                default: return "Problem";
            }
        }

        // ------------------------------------------------------------------ launcher + metrics events

        void OnLauncherChanged()
        {
            UpdatePill();
            Server.RefreshState();
            Chat.RefreshState();
            if (tray != null) tray.Update(Launcher, StateTitle(Launcher.State));
            MaybeScreenshot();
        }

        void OnMetrics(Dictionary<string, object> m)
        {
            UpdatePill();
            if (tab == "monitor") Monitor.Render(m);
            if (tab == "about") About.Render(m);
        }

        void Tick()
        {
            string line;
            System.Text.StringBuilder sb = null;
            while (pendingLog.TryDequeue(out line))
            {
                if (sb == null) sb = new System.Text.StringBuilder();
                sb.Append(line).Append("\r\n");
            }
            if (sb != null) Server.AppendLog(sb.ToString());
            Server.UpdateUptime();
        }

        // ------------------------------------------------------------------ install + settings

        public void LoadInstall()
        {
            string dir = StrataInstall.Find(Settings.StrataDir);
            Launcher.Dir = dir ?? "";
            if (dir != null && dir != Settings.StrataDir) { Settings.StrataDir = dir; Settings.Save(); }
            Models = StrataInstall.Models(dir);
            SelectedModel = null;
            foreach (ModelEntry m in Models)
                if (string.Equals(m.FileName, Settings.Config, StringComparison.OrdinalIgnoreCase)) SelectedModel = m;
            if (SelectedModel == null && Models.Count > 0) SelectedModel = Models[0];
            if (!Launcher.IsActive) Launcher.Model = SelectedModel;
        }

        public void SaveSettings()
        {
            if (SelectedModel != null) Settings.Config = SelectedModel.FileName;
            Launcher.KeepRunning = Settings.KeepRunning;
            Settings.Save();
        }

        public void SetTheme(bool dark, bool save)
        {
            Theme.Apply(dark);
            if (save) { Settings.Theme = dark ? "dark" : "light"; Settings.Save(); }
            themeBtn.Content = new IconView(dark ? "sun" : "moon", 20);
            About.RefreshTheme();
        }

        // ------------------------------------------------------------------ server actions

        public void StartServer()
        {
            SaveSettings();
            ModelEntry m = SelectedModel;
            string mode = Settings.Mode;
            int idle = Settings.IdleMinutes;
            ThreadPool.QueueUserWorkItem(delegate { Launcher.Start(m, mode, idle); });
        }

        public void StopServer() { ThreadPool.QueueUserWorkItem(delegate { Launcher.Stop(); }); }

        public void RestartServer()
        {
            SaveSettings();
            ModelEntry m = SelectedModel;
            string mode = Settings.Mode;
            int idle = Settings.IdleMinutes;
            ThreadPool.QueueUserWorkItem(delegate { Launcher.Stop(); Launcher.Start(m, mode, idle); });
        }

        public void GpuToggle()
        {
            bool unload = Launcher.Loaded;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string r = unload ? Launcher.Unload() : Launcher.Load();
                pendingLog.Enqueue("[launcher] " + (unload ? "unload" : "load") + ": " + r);
            });
        }

        public void CopyUrl()
        {
            Ui.Copy(Launcher.ApiUrl);
            Toast("success", "Copied", Launcher.ApiUrl, 2000);
        }

        // ------------------------------------------------------------------ toasts

        public void Toast(string kind, string title, string text, int ms) { Toast(kind, title, text, ms, null, null); }

        public void Toast(string kind, string title, string text, int ms, string actionLabel, Action action)
        {
            string icon = kind == "success" ? "check" : kind == "warn" ? "warning" : kind == "error" ? "error" : "info";
            string tone = kind == "success" ? "StAccent" : kind == "warn" ? "StWarn" : kind == "error" ? "StDanger" : "StInfo";
            Border card = new Border();
            card.SetResourceReference(Border.BackgroundProperty, "StSurface");
            card.SetResourceReference(Border.BorderBrushProperty, "StLine");
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(14);
            card.Padding = new Thickness(16, 14, 16, 14);
            card.MaxWidth = 420;
            card.Margin = new Thickness(0, 10, 0, 0);
            card.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.18, BlurRadius = 24, ShadowDepth = 6, Direction = 270 };
            Grid g = new Grid();
            g.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            g.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            IconView iv = new IconView(icon, 20);
            iv.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, tone);
            iv.VerticalAlignment = VerticalAlignment.Top;
            iv.Margin = new Thickness(0, 0, 12, 0);
            g.Children.Add(Ui.At(iv, 0, 0));
            StackPanel sp = new StackPanel();
            sp.Children.Add(Ui.Text(title, 14, "StInk", "bold", true));
            if (!string.IsNullOrEmpty(text)) { TextBlock t = Ui.Text(text, 14, "StInkSoft", "regular", true); sp.Children.Add(t); }
            g.Children.Add(Ui.At(sp, 0, 1));
            DispatcherTimer timer = new DispatcherTimer();
            if (action != null)
            {
                g.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
                Button b = Ui.Btn("secondary", actionLabel, null, delegate { timer.Stop(); toasts.Children.Remove(card); action(); });
                b.Height = 30; b.Padding = new Thickness(12, 0, 12, 0); b.FontSize = 13; b.Margin = new Thickness(12, 0, 0, 0);
                b.VerticalAlignment = VerticalAlignment.Center;
                g.Children.Add(Ui.At(b, 0, 2));
            }
            card.Child = g;
            toasts.Children.Add(card);
            timer.Interval = TimeSpan.FromMilliseconds(ms);
            timer.Tick += delegate { timer.Stop(); toasts.Children.Remove(card); };
            timer.Start();
        }

        // ------------------------------------------------------------------ hooks for --uitest

        public bool IsPageVisible(string name) { FrameworkElement p; return pages.TryGetValue(name, out p) && p.Visibility == Visibility.Visible; }

        public RadioButton TabButton(string name) { return tabs[name]; }

        public Button ThemeButton { get { return themeBtn; } }

        public int ToastCount { get { return toasts.Children.Count; } }

        public string[] TabNames { get { string[] k = new string[pages.Count]; pages.Keys.CopyTo(k, 0); return k; } }

        // ------------------------------------------------------------------ show / hide / exit

        public void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (exiting) return;
            e.Cancel = true;
            Hide();
            if (!toldAboutTray)
            {
                toldAboutTray = true;
                tray.Balloon("StrataHome is still running", "It lives in the tray. Right-click the icon for the menu.", false);
            }
        }

        void TrimSoon()
        {
            DispatcherTimer t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += delegate { t.Stop(); if (!IsVisible) Native.TrimMemory(); };
            t.Start();
        }

        public void ExitApp()
        {
            exiting = true;
            SaveSettings();
            Metrics.Stop();
            if (tray != null) tray.Dispose();
            Launcher.KeepRunning = Settings.KeepRunning;
            Launcher.Dispose();
            Application.Current.Shutdown();
        }

        // ------------------------------------------------------------------ --screenshot (used for the docs and the tests)

        void MaybeScreenshot()
        {
            if (Opt.ScreenshotPath == null || shotScheduled) return;
            if (!Launcher.Loaded || !(Launcher.State == RunState.Ready || Launcher.State == RunState.External)) return;
            shotScheduled = true;
            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromSeconds(Math.Max(0.5, Opt.DelaySeconds));
            t.Tick += delegate
            {
                t.Stop();
                if (Opt.Drawer) Drawer.Open();
                if (Opt.Prompt != null && tab == "chat") Chat.Demo(Opt.Prompt, delegate { Capture(); });
                else Capture();
            };
            t.Start();
        }

        void Capture()
        {
            try
            {
                Tick();
                Server.Redact();
                Chat.Redact();
                DispatcherTimer settle = new DispatcherTimer();
                settle.Interval = TimeSpan.FromMilliseconds(600);
                settle.Tick += delegate
                {
                    settle.Stop();
                    try { WindowShot.Save(new WindowInteropHelper(this).Handle, Opt.ScreenshotPath); }
                    catch (Exception ex) { Paths.Diag("screenshot failed: " + ex.Message); }
                };
                settle.Start();
            }
            catch (Exception ex) { Paths.Diag("screenshot failed: " + ex.Message); }
        }
    }
}

