using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace StrataHome
{
    /// <summary>The Server tab: start, stop, which model, the memory mode, the options and the live server log.</summary>
    internal sealed class ServerView
    {
        public readonly FrameworkElement Root;
        readonly MainWindow w;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "StrataHome";

        Ellipse dot;
        TextBlock stateText, detailText, folderText, uptimeText, endpointText;
        Button btnStart, btnStop, btnRestart, btnGpu, btnCheck, btnUpdate, changeFolder;
        TextBlock updateStatus;
        ToggleButton tUpdate;
        StackPanel modelRows;
        readonly List<RadioButton> modeRows = new List<RadioButton>();
        TextBox idleBox;
        Grid idleRow;
        ToggleButton tAuto, tWin, tKeep;
        TextBox log;
        bool building;

        public ServerView(MainWindow window)
        {
            w = window;
            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            StackPanel col = new StackPanel();
            col.MaxWidth = 860;
            col.Margin = new Thickness(24);
            col.Children.Add(Spaced(BuildStatus()));
            col.Children.Add(Spaced(BuildModel()));
            col.Children.Add(Spaced(BuildOptions()));
            col.Children.Add(Spaced(BuildUpdates()));
            col.Children.Add(BuildLog());
            sv.Content = col;
            Root = sv;
            RefreshAll();
        }

        static FrameworkElement Spaced(FrameworkElement e) { e.Margin = new Thickness(0, 0, 0, 20); return e; }

        // ------------------------------------------------------------------ status + buttons

        FrameworkElement BuildStatus()
        {
            StackPanel sp = new StackPanel();
            Grid head = new Grid();
            head.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            head.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            dot = new Ellipse { Width = 14, Height = 14, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(Ui.At(dot, 0, 0));
            StackPanel t = new StackPanel();
            stateText = Ui.Text("Stopped", 24, "StInk", "black");
            detailText = Ui.Text("", 14, "StInkMuted", "regular", true);
            t.Children.Add(stateText); t.Children.Add(detailText);
            head.Children.Add(Ui.At(t, 0, 1));
            sp.Children.Add(head);

            WrapPanel btns = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
            btnStart = Ui.Btn("primary", "Start", null, delegate { w.StartServer(); });
            btnStop = Ui.Btn("secondary", "Stop", null, delegate { w.StopServer(); });
            btnRestart = Ui.Btn("secondary", "Restart", null, delegate { w.RestartServer(); });
            btnGpu = Ui.Btn("secondary", "Free GPU", null, delegate { w.GpuToggle(); });
            Button copy = Ui.Btn("secondary", "Copy API URL", null, delegate { w.CopyUrl(); });
            foreach (Button b in new Button[] { btnStart, btnStop, btnRestart, btnGpu, copy }) { b.Margin = new Thickness(0, 0, 10, 8); btns.Children.Add(b); }
            sp.Children.Add(btns);

            Grid facts = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            facts.ColumnDefinitions.Add(Ui.Col(new GridLength(160)));
            facts.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            facts.RowDefinitions.Add(Ui.Row(Ui.Auto)); facts.RowDefinitions.Add(Ui.Row(Ui.Auto));
            facts.Children.Add(Ui.At(Ui.Text("Endpoint", 14, "StInkMuted"), 0, 0));
            endpointText = Ui.Mono("", 13, "StInkSoft");
            endpointText.Margin = new Thickness(0, 2, 0, 2);
            facts.Children.Add(Ui.At(endpointText, 0, 1));
            facts.Children.Add(Ui.At(Ui.Text("Uptime", 14, "StInkMuted"), 1, 0));
            uptimeText = Ui.Text("-", 14, "StInkSoft");
            uptimeText.Margin = new Thickness(0, 2, 0, 2);
            facts.Children.Add(Ui.At(uptimeText, 1, 1));
            sp.Children.Add(facts);
            return Ui.Card(sp);
        }

        // ------------------------------------------------------------------ model + memory

        FrameworkElement BuildModel()
        {
            StackPanel sp = new StackPanel();
            sp.Children.Add(Ui.Text("Model", 16, "StInk", "bold"));
            modelRows = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            sp.Children.Add(modelRows);

            TextBlock mt = Ui.Text("Memory", 16, "StInk", "bold");
            mt.Margin = new Thickness(0, 22, 0, 0);
            sp.Children.Add(mt);
            TextBlock mn = Ui.Text("The model takes tens of GB of RAM and VRAM while it is loaded. Applies the next time Strata starts.", 13, "StInkMuted", "regular", true);
            mn.Margin = new Thickness(0, 6, 0, 10);
            sp.Children.Add(mn);
            string[][] modes = new string[][]
            {
                new string[] { "always", "Keep the model loaded", "Fastest replies; holds the memory the whole time." },
                new string[] { "idle", "Unload when idle", "Frees the GPU and RAM after a while without requests; the next request loads it again." },
                new string[] { "ondemand", "Load on first request", "Nothing is loaded at start; also unloads when idle." }
            };
            foreach (string[] m in modes)
            {
                RadioButton r = new RadioButton();
                r.Style = Ui.Style("StRadioRow");
                r.GroupName = "mode";
                r.Tag = m[0];
                StackPanel c = new StackPanel();
                c.Children.Add(Ui.Text(m[1], 14, "StInk", "bold"));
                c.Children.Add(Ui.Text(m[2], 12, "StInkMuted", "regular", true));
                r.Content = c;
                r.Margin = new Thickness(0, 0, 0, 8);
                r.Checked += delegate
                {
                    if (building) return;
                    w.Settings.Mode = (string)r.Tag;
                    w.SaveSettings();
                    idleRow.Visibility = w.Settings.Mode == "always" ? Visibility.Collapsed : Visibility.Visible;
                };
                modeRows.Add(r);
                sp.Children.Add(r);
            }
            idleRow = new Grid { Margin = new Thickness(4, 4, 0, 0) };
            idleRow.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            idleRow.ColumnDefinitions.Add(Ui.Col(new GridLength(90)));
            idleRow.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            Grid.SetColumn(idleRow, 0);
            TextBlock a = Ui.Text("Unload after", 14, "StInkSoft");
            a.VerticalAlignment = VerticalAlignment.Center; a.Margin = new Thickness(0, 0, 10, 0);
            idleRow.Children.Add(Ui.At(a, 0, 0));
            idleBox = new TextBox { Style = Ui.Style("StInput"), Height = 38, Text = "10" };
            idleBox.TextChanged += delegate
            {
                if (building) return;
                int n;
                if (int.TryParse(idleBox.Text.Trim(), out n) && n >= 1 && n <= 1440) { w.Settings.IdleMinutes = n; w.SaveSettings(); }
            };
            idleRow.Children.Add(Ui.At(idleBox, 0, 1));
            TextBlock b2 = Ui.Text("minutes without a request", 14, "StInkSoft");
            b2.VerticalAlignment = VerticalAlignment.Center; b2.Margin = new Thickness(10, 0, 0, 0);
            idleRow.Children.Add(Ui.At(b2, 0, 2));
            sp.Children.Add(idleRow);
            return Ui.Card(sp);
        }

        // ------------------------------------------------------------------ options

        FrameworkElement BuildOptions()
        {
            StackPanel sp = new StackPanel();
            sp.Children.Add(Ui.Text("Options", 16, "StInk", "bold"));
            tAuto = Ui.Toggle(w.Settings.AutoStartServer, delegate(bool on) { w.Settings.AutoStartServer = on; w.SaveSettings(); });
            tKeep = Ui.Toggle(w.Settings.KeepRunning, delegate(bool on) { w.Settings.KeepRunning = on; w.SaveSettings(); });
            tWin = Ui.Toggle(GetWindowsStartup(), delegate(bool on) { SetWindowsStartup(on); });
            sp.Children.Add(Row("Start Strata when this app opens", "Otherwise use Start on this page.", tAuto));
            sp.Children.Add(Row("Start this app with Windows", "Minimized to the tray. Off by default.", tWin));
            sp.Children.Add(Row("Leave Strata running when this app exits", "Otherwise closing the app stops the model, so it is never left running by accident.", tKeep));

            Grid f = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            f.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            f.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            StackPanel ft = new StackPanel();
            ft.Children.Add(Ui.Text("Strata folder", 13, "StInkSoft", "medium"));
            folderText = Ui.Mono("", 12, "StInkMuted");
            folderText.TextTrimming = TextTrimming.CharacterEllipsis;
            ft.Children.Add(folderText);
            f.Children.Add(Ui.At(ft, 0, 0));
            Button change = changeFolder = Ui.Btn("secondary", "Change...", null, delegate { PickFolder(); });
            change.Height = 36; change.Margin = new Thickness(12, 0, 0, 0);
            f.Children.Add(Ui.At(change, 0, 1));
            sp.Children.Add(f);
            return Ui.Card(sp);
        }

        static FrameworkElement Row(string title, string sub, ToggleButton t)
        {
            Grid g = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            g.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            g.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            StackPanel sp = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            sp.Children.Add(Ui.Text(title, 14, "StInkSoft", "medium"));
            sp.Children.Add(Ui.Text(sub, 12, "StInkMuted", "regular", true));
            g.Children.Add(Ui.At(sp, 0, 0));
            t.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(Ui.At(t, 0, 1));
            return g;
        }

        FrameworkElement BuildUpdates()
        {
            StackPanel sp = new StackPanel();
            sp.Children.Add(Ui.Text("Strata updates", 16, "StInk", "bold"));
            tUpdate = Ui.Toggle(w.Settings.AutoUpdateStrata, delegate(bool on) { w.Settings.AutoUpdateStrata = on; w.SaveSettings(); });
            sp.Children.Add(Row("Automatically update Strata", "Checks official stable releases on startup and every six hours while this app runs. Installs after 30 seconds without requests, then restarts the same model.", tUpdate));
            updateStatus = Ui.Text(w.Updater.Status, 13, "StInkSoft", "regular", true);
            updateStatus.Margin = new Thickness(0, 16, 0, 12);
            sp.Children.Add(updateStatus);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal };
            btnCheck = Ui.Btn("secondary", "Check now", null, delegate { w.Updater.CheckNow(); });
            btnUpdate = Ui.Btn("primary", "Update now", null, delegate { w.Updater.UpdateNow(); });
            btnUpdate.Margin = new Thickness(10, 0, 0, 0);
            buttons.Children.Add(btnCheck); buttons.Children.Add(btnUpdate);
            sp.Children.Add(buttons);
            sp.Children.Add(Ui.Text("Keeps your models and settings. A recovery copy is saved before each update. This updates Strata; launcher updates are available on StrataHome's release page.", 12, "StInkMuted", "regular", true));
            return Ui.Card(sp);
        }

        // ------------------------------------------------------------------ log

        FrameworkElement BuildLog()
        {
            StackPanel sp = new StackPanel();
            Grid head = new Grid();
            head.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            head.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            head.Children.Add(Ui.At(Ui.Text("Server log", 16, "StInk", "bold"), 0, 0));
            Button open = Ui.Btn("secondary", "Open log folder", null, delegate
            {
                try { Directory.CreateDirectory(Paths.Logs); Process.Start("explorer.exe", Paths.Logs); } catch { }
            });
            open.Height = 32; open.FontSize = 13; open.Padding = new Thickness(12, 0, 12, 0);
            head.Children.Add(Ui.At(open, 0, 1));
            sp.Children.Add(head);
            Border box = new Border { CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 12, 0, 0) };
            box.SetResourceReference(Border.BackgroundProperty, "StCodeBg");
            log = new TextBox();
            log.IsReadOnly = true;
            log.BorderThickness = new Thickness(0);
            log.Background = Brushes.Transparent;
            log.FontFamily = Fonts.Mono;
            log.FontSize = 12;
            log.Padding = new Thickness(14);
            log.Height = 300;
            log.TextWrapping = TextWrapping.Wrap;
            log.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            log.SetResourceReference(Control.ForegroundProperty, "StCodeInk");
            log.FocusVisualStyle = null;
            box.Child = log;
            sp.Children.Add(box);
            return Ui.Card(sp);
        }

        public int ModelRowCount { get { return modelRows.Children.Count; } }
        public bool UpdateControlsPresent { get { return btnCheck != null && btnUpdate != null && tUpdate != null && updateStatus != null; } }
        public bool UpdateControlsLocked { get { return !btnStart.IsEnabled && !btnStop.IsEnabled && !btnRestart.IsEnabled && !btnGpu.IsEnabled && !changeFolder.IsEnabled && !btnCheck.IsEnabled && !btnUpdate.IsEnabled; } }

        public void AppendLog(string s)
        {
            log.AppendText(s);
            if (log.Text.Length > 80000) log.Text = log.Text.Substring(log.Text.Length - 40000);
            log.ScrollToEnd();
        }

        public void Redact()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            log.Text = log.Text.Replace(home, @"C:\Users\you");
            folderText.Text = folderText.Text.Replace(home, @"C:\Users\you");
        }

        // ------------------------------------------------------------------ state

        public void RefreshAll()
        {
            building = true;
            try
            {
                modelRows.Children.Clear();
                if (w.Models.Count == 0)
                {
                    modelRows.Children.Add(Ui.Text(w.Launcher.Dir.Length == 0
                        ? "Strata was not found. Click Change... to point at its folder."
                        : "No installed model found in this Strata folder. Run START-HERE.bat once.", 14, "StDangerText", "regular", true));
                }
                foreach (ModelEntry m in w.Models)
                {
                    RadioButton r = new RadioButton { Style = Ui.Style("StRadioRow"), GroupName = "model", Tag = m, Margin = new Thickness(0, 0, 0, 8) };
                    r.Content = Ui.Text(m.Label, 14, "StInk", "bold");
                    r.IsChecked = m == w.SelectedModel;
                    r.Checked += delegate
                    {
                        if (building) return;
                        w.SelectedModel = (ModelEntry)r.Tag;
                        if (!w.Launcher.IsActive) w.Launcher.Model = w.SelectedModel;
                        w.SaveSettings();
                    };
                    modelRows.Children.Add(r);
                }
                foreach (RadioButton r in modeRows) r.IsChecked = (string)r.Tag == w.Settings.Mode;
                idleBox.Text = w.Settings.IdleMinutes.ToString();
                idleRow.Visibility = w.Settings.Mode == "always" ? Visibility.Collapsed : Visibility.Visible;
                folderText.Text = w.Launcher.Dir.Length > 0 ? w.Launcher.Dir : "not found";
                tUpdate.IsChecked = w.Settings.AutoUpdateStrata;
                tAuto.IsChecked = w.Settings.AutoStartServer;
                tKeep.IsChecked = w.Settings.KeepRunning;
                tWin.IsChecked = GetWindowsStartup();
            }
            finally { building = false; }
            RefreshState();
        }

        public void RefreshState()
        {
            Launcher l = w.Launcher;
            RunState s = l.State;
            string key = s == RunState.Ready || s == RunState.External ? "StAccent" : s == RunState.Unloaded ? "StInfo"
                : s == RunState.Error ? "StDanger" : s == RunState.Stopped ? "StInkMuted" : "StWarn";
            dot.SetResourceReference(Shape.FillProperty, key);
            stateText.Text = s == RunState.External && !l.Loaded ? "Idle \u00b7 model unloaded" : MainWindow.StateTitle(s);
            string d = l.Detail;
            if (s == RunState.External) d = "Started outside this app. StrataHome is attached to it.";
            if (l.Dir.Length == 0 && s != RunState.Starting) d = "Strata was not found. Click Change... below to point at its folder.";
            detailText.Text = d;
            detailText.Visibility = string.IsNullOrEmpty(d) ? Visibility.Collapsed : Visibility.Visible;

            bool updating = w.Updater.Busy;
            bool active = l.IsActive || updating;
            bool ready = s == RunState.Ready || s == RunState.Unloaded || s == RunState.External;
            btnStart.IsEnabled = !active && w.Models.Count > 0;
            btnStop.IsEnabled = active && !updating && s != RunState.Stopping;
            btnRestart.IsEnabled = btnStop.IsEnabled;
            btnGpu.IsEnabled = ready && !updating;
            changeFolder.IsEnabled = !active && !w.Updater.Checking;
            tKeep.IsEnabled = !updating;
            tUpdate.IsEnabled = !updating;
            btnCheck.IsEnabled = !updating && !w.Updater.Checking;
            btnUpdate.IsEnabled = !updating && !w.Updater.Checking && w.Updater.Available;
            updateStatus.Text = w.Updater.Status;
            btnGpu.Content = l.Loaded ? "Free GPU" : "Load now";
            foreach (UIElement u in modelRows.Children) u.IsEnabled = !active;
            foreach (RadioButton r in modeRows) r.IsEnabled = !active;
            idleBox.IsEnabled = !active;
            endpointText.Text = l.ApiUrl;
            UpdateUptime();
        }

        public void UpdateUptime()
        {
            Launcher l = w.Launcher;
            if (!l.IsActive) { uptimeText.Text = "-"; return; }
            TimeSpan t = DateTime.UtcNow - l.StartedUtc;
            string up = ((int)t.TotalHours).ToString() + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
            if (l.State == RunState.External) uptimeText.Text = "attached for " + up;
            else uptimeText.Text = l.LoadSeconds >= 0 ? up + "   (model loaded in " + l.LoadSeconds.ToString("0") + " s)" : up;
        }

        // ------------------------------------------------------------------ folder + Windows startup

        void PickFolder()
        {
            if (w.Updater.Busy || w.Launcher.IsActive) return;
            using (System.Windows.Forms.FolderBrowserDialog d = new System.Windows.Forms.FolderBrowserDialog())
            {
                d.Description = "Choose the folder where Strata is installed (it contains serve\\server.py and .venv).";
                if (!string.IsNullOrEmpty(w.Settings.StrataDir)) d.SelectedPath = w.Settings.StrataDir;
                if (d.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                if (!StrataInstall.IsValid(d.SelectedPath))
                {
                    w.Toast("warn", "That does not look like a Strata install", "It should contain serve\\server.py and .venv\\Scripts\\python.exe. Run Strata's START-HERE.bat once to set it up.", 7000);
                    return;
                }
                w.Settings.StrataDir = d.SelectedPath;
                w.Settings.Save();
                w.LoadInstall();
                RefreshAll();
            }
        }

        static bool GetWindowsStartup()
        {
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null; }
            catch { return false; }
        }

        static void SetWindowsStartup(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunName, "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --minimized");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch { }
        }
    }
}
