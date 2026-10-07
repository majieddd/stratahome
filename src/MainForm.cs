using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace StrataHome
{
    internal sealed class MainForm : Form
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "StrataHome";

        readonly Launcher launcher = new Launcher();
        readonly Settings settings = Settings.Load();
        readonly ConcurrentQueue<string> pendingLog = new ConcurrentQueue<string>();
        readonly System.Windows.Forms.Timer ticker = new System.Windows.Forms.Timer();
        List<ModelEntry> models = new List<ModelEntry>();
        ChatForm chat;
        bool allowVisible = true;
        bool exiting;
        bool toldAboutTray;
        bool refreshing;
        RunState previous = RunState.Stopped;
        Color stateColor = Color.Gray;
        public string ScreenshotPath;                      // --screenshot <file>: save the window once Strata is ready
        bool shotScheduled;
        public string ChatShotPath;                        // --screenshot-chat <file> [--prompt <text>]
        public string ChatPrompt = "Give me three tips for writing clear commit messages.";
        bool chatShotScheduled;

        // controls
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Panel dot = new Panel();
        readonly Label lblState = new Label();
        readonly Label lblDetail = new Label();
        readonly Label valModel = new Label();
        readonly Label valContext = new Label();
        readonly Label valEndpoint = new Label();
        readonly Label valUptime = new Label();
        readonly Button btnStart = new Button();
        readonly Button btnStop = new Button();
        readonly Button btnRestart = new Button();
        readonly Button btnChat = new Button();
        readonly Button btnCopy = new Button();
        readonly Button btnGpu = new Button();
        readonly ComboBox cmbModel = new ComboBox();
        readonly ComboBox cmbMode = new ComboBox();
        readonly NumericUpDown numIdle = new NumericUpDown();
        readonly CheckBox chkAuto = new CheckBox();
        readonly CheckBox chkWindows = new CheckBox();
        readonly CheckBox chkKeep = new CheckBox();
        readonly Label lblDir = new Label();
        readonly TextBox logBox = new TextBox();
        ToolStripMenuItem miStart, miStop, miRestart, miChat, miGpu;

        public MainForm(bool startMinimized, bool noAutoStart, string modeOverride, int idleOverride)
        {
            allowVisible = !startMinimized;
            // --mode and --idle-minutes win over the saved settings (and are saved like any change made in the window)
            if (modeOverride == "always" || modeOverride == "idle" || modeOverride == "ondemand") settings.Mode = modeOverride;
            if (idleOverride > 0) settings.IdleMinutes = Math.Min(1440, idleOverride);
            Paths.Diag("settings in effect: mode " + settings.Mode + ", idle " + settings.IdleMinutes + " min, auto-start " + settings.AutoStartServer);
            BuildUi();
            BuildTray();
            IntPtr force = Handle;                          // make sure BeginInvoke works even while the window is hidden

            launcher.Changed += delegate { PostRefresh(); };
            launcher.LogLine += delegate(string l) { pendingLog.Enqueue(l); };
            ticker.Interval = 500;
            ticker.Tick += delegate { Tick(); };
            ticker.Start();

            LoadInstall();
            RefreshUi();
            if (settings.AutoStartServer && !noAutoStart && models.Count > 0) StartServer();
        }

        // ------------------------------------------------------------------ UI construction

        void BuildUi()
        {
            Text = "StrataHome";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(580, 660);
            MinimumSize = new Size(560, 560);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // header
            Panel header = new Panel(); header.Dock = DockStyle.Top; header.Height = 76; header.BackColor = Color.White;
            dot.Size = new Size(18, 18); dot.Location = new Point(20, 24);
            dot.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(stateColor)) e.Graphics.FillEllipse(b, 1, 1, 15, 15);
            };
            lblState.Font = new Font("Segoe UI Semibold", 15f); lblState.Location = new Point(48, 12); lblState.AutoSize = true;
            lblDetail.ForeColor = Color.DimGray; lblDetail.Location = new Point(50, 45); lblDetail.Size = new Size(510, 20);
            lblDetail.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            header.Controls.AddRange(new Control[] { dot, lblState, lblDetail });

            // facts
            Panel info = new Panel(); info.Dock = DockStyle.Top; info.Height = 104; info.Padding = new Padding(20, 10, 20, 6);
            AddFact(info, "Model", valModel, 10);
            AddFact(info, "Context", valContext, 34);
            AddFact(info, "Endpoint", valEndpoint, 58);
            AddFact(info, "Uptime", valUptime, 82);

            // actions
            FlowLayoutPanel actions = new FlowLayoutPanel(); actions.Dock = DockStyle.Top; actions.Height = 46;
            actions.Padding = new Padding(16, 6, 8, 4);
            Setup(btnStart, "Start", 76, delegate { StartServer(); });
            Setup(btnStop, "Stop", 76, delegate { StopServer(); });
            Setup(btnRestart, "Restart", 76, delegate { RestartServer(); });
            Setup(btnChat, "Chat...", 76, delegate { OpenChat(); });
            Setup(btnCopy, "Copy API URL", 110, delegate { CopyUrl(); });
            Setup(btnGpu, "Free GPU", 90, delegate { GpuToggle(); });
            actions.Controls.AddRange(new Control[] { btnStart, btnStop, btnRestart, btnChat, btnCopy, btnGpu });

            // settings
            GroupBox box = new GroupBox(); box.Text = "Settings"; box.Dock = DockStyle.Top; box.Height = 178;
            box.Padding = new Padding(12, 6, 12, 6);
            Label l1 = new Label(); l1.Text = "Model"; l1.Location = new Point(16, 28); l1.AutoSize = true;
            cmbModel.DropDownStyle = ComboBoxStyle.DropDownList; cmbModel.Location = new Point(86, 24); cmbModel.Width = 330;
            cmbModel.SelectedIndexChanged += delegate { if (!refreshing) SaveFromUi(); };
            Label l2 = new Label(); l2.Text = "Memory"; l2.Location = new Point(16, 58); l2.AutoSize = true;
            cmbMode.DropDownStyle = ComboBoxStyle.DropDownList; cmbMode.Location = new Point(86, 54); cmbMode.Width = 330;
            cmbMode.Items.AddRange(new object[] {
                "Keep the model loaded (fastest replies)",
                "Unload when idle (frees GPU and RAM)",
                "Load on first request, unload when idle" });
            cmbMode.SelectedIndexChanged += delegate { numIdle.Enabled = cmbMode.SelectedIndex > 0; if (!refreshing) SaveFromUi(); };
            Label l3 = new Label(); l3.Text = "after"; l3.Location = new Point(428, 58); l3.AutoSize = true;
            numIdle.Minimum = 1; numIdle.Maximum = 1440; numIdle.Value = 10; numIdle.Width = 56; numIdle.Location = new Point(466, 54);
            numIdle.ValueChanged += delegate { if (!refreshing) SaveFromUi(); };
            Label l4 = new Label(); l4.Text = "min"; l4.Location = new Point(526, 58); l4.AutoSize = true;
            chkAuto.Text = "Start Strata when this app opens"; chkAuto.Location = new Point(18, 86); chkAuto.AutoSize = true;
            chkWindows.Text = "Start this app with Windows (minimized to the tray)"; chkWindows.Location = new Point(18, 108); chkWindows.AutoSize = true;
            chkKeep.Text = "Leave Strata running when this app exits"; chkKeep.Location = new Point(18, 130); chkKeep.AutoSize = true;
            chkAuto.CheckedChanged += delegate { if (!refreshing) SaveFromUi(); };
            chkKeep.CheckedChanged += delegate { if (!refreshing) SaveFromUi(); };
            chkWindows.CheckedChanged += delegate { if (!refreshing) SetWindowsStartup(chkWindows.Checked); };
            lblDir.Location = new Point(18, 154); lblDir.Size = new Size(430, 18); lblDir.ForeColor = Color.DimGray;
            lblDir.AutoEllipsis = true;
            Button change = new Button(); change.Text = "Change..."; change.Size = new Size(84, 24); change.Location = new Point(466, 148);
            change.Click += delegate { PickFolder(); };
            box.Controls.AddRange(new Control[] { l1, cmbModel, l2, cmbMode, l3, numIdle, l4, chkAuto, chkWindows, chkKeep, lblDir, change });

            logBox.Multiline = true; logBox.ReadOnly = true; logBox.ScrollBars = ScrollBars.Vertical; logBox.WordWrap = true;
            logBox.Font = new Font("Consolas", 8.5f); logBox.Dock = DockStyle.Fill; logBox.BackColor = Color.FromArgb(250, 250, 250);

            Controls.Add(logBox);
            Controls.Add(box);
            Controls.Add(actions);
            Controls.Add(info);
            Controls.Add(header);

            FormClosing += OnClosing;
            Dpi.Apply(this);
        }

        void AddFact(Panel p, string label, Label value, int y)
        {
            Label l = new Label(); l.Text = label; l.ForeColor = Color.DimGray; l.Location = new Point(20, y); l.Size = new Size(70, 20);
            value.Location = new Point(96, y); value.Size = new Size(450, 20);
            value.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; value.AutoEllipsis = true;
            p.Controls.Add(l); p.Controls.Add(value);
        }

        static void Setup(Button b, string text, int width, EventHandler click)
        {
            b.Text = text; b.Size = new Size(width, 28); b.Margin = new Padding(4, 2, 4, 2); b.Click += click;
        }

        void BuildTray()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            ToolStripMenuItem show = new ToolStripMenuItem("Show StrataHome");
            show.Font = new Font(show.Font, FontStyle.Bold);
            show.Click += delegate { ShowFromTray(); };
            miStart = new ToolStripMenuItem("Start Strata"); miStart.Click += delegate { StartServer(); };
            miStop = new ToolStripMenuItem("Stop Strata"); miStop.Click += delegate { StopServer(); };
            miRestart = new ToolStripMenuItem("Restart"); miRestart.Click += delegate { RestartServer(); };
            miChat = new ToolStripMenuItem("Chat..."); miChat.Click += delegate { OpenChat(); };
            ToolStripMenuItem copy = new ToolStripMenuItem("Copy API URL"); copy.Click += delegate { CopyUrl(); };
            miGpu = new ToolStripMenuItem("Free GPU now"); miGpu.Click += delegate { GpuToggle(); };
            ToolStripMenuItem logs = new ToolStripMenuItem("Open log folder");
            logs.Click += delegate { try { Directory.CreateDirectory(Paths.Logs); Process.Start("explorer.exe", Paths.Logs); } catch { } };
            ToolStripMenuItem exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { ExitApp(); };
            m.Items.AddRange(new ToolStripItem[] { show, new ToolStripSeparator(), miStart, miStop, miRestart, new ToolStripSeparator(),
                miChat, copy, miGpu, new ToolStripSeparator(), logs, exit });
            tray.ContextMenuStrip = m;
            tray.Icon = Icons.For(RunState.Stopped);
            tray.Text = "StrataHome";
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowFromTray(); };
            tray.BalloonTipClicked += delegate { ShowFromTray(); };
        }

        // ------------------------------------------------------------------ show / hide / exit

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(allowVisible && value);
        }

        public void ShowFromTray()
        {
            allowVisible = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!toldAboutTray)
                {
                    toldAboutTray = true;
                    tray.ShowBalloonTip(3000, "StrataHome is still running", "It lives in the tray. Right-click the icon for the menu.", ToolTipIcon.Info);
                }
            }
        }

        void ExitApp()
        {
            exiting = true;
            ticker.Stop();
            SaveFromUi();
            tray.Visible = false;
            tray.Dispose();
            launcher.KeepRunning = settings.KeepRunning;
            launcher.Dispose();
            Application.Exit();
        }

        // ------------------------------------------------------------------ install + settings

        void LoadInstall()
        {
            string dir = StrataInstall.Find(settings.StrataDir);
            launcher.Dir = dir ?? "";
            if (dir != null && dir != settings.StrataDir) { settings.StrataDir = dir; settings.Save(); }
            models = StrataInstall.Models(dir);
            refreshing = true;
            try
            {
                cmbModel.Items.Clear();
                foreach (ModelEntry m in models) cmbModel.Items.Add(m);
                int pick = 0;
                for (int i = 0; i < models.Count; i++)
                    if (string.Equals(models[i].FileName, settings.Config, StringComparison.OrdinalIgnoreCase)) pick = i;
                if (models.Count > 0) cmbModel.SelectedIndex = pick;
                cmbMode.SelectedIndex = settings.Mode == "idle" ? 1 : settings.Mode == "ondemand" ? 2 : 0;
                numIdle.Value = Math.Max(numIdle.Minimum, Math.Min(numIdle.Maximum, settings.IdleMinutes));
                numIdle.Enabled = cmbMode.SelectedIndex > 0;
                chkAuto.Checked = settings.AutoStartServer;
                chkKeep.Checked = settings.KeepRunning;
                chkWindows.Checked = GetWindowsStartup();
                lblDir.Text = dir != null ? "Strata folder: " + dir : "Strata folder: not found";
            }
            finally { refreshing = false; }
        }

        void SaveFromUi()
        {
            ModelEntry m = cmbModel.SelectedItem as ModelEntry;
            if (m != null) settings.Config = m.FileName;
            settings.Mode = cmbMode.SelectedIndex == 1 ? "idle" : cmbMode.SelectedIndex == 2 ? "ondemand" : "always";
            settings.IdleMinutes = (int)numIdle.Value;
            settings.AutoStartServer = chkAuto.Checked;
            settings.KeepRunning = chkKeep.Checked;
            launcher.KeepRunning = chkKeep.Checked;
            settings.Save();
        }

        void PickFolder()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "Choose the folder where Strata is installed (it contains serve\\server.py and .venv).";
                if (!string.IsNullOrEmpty(settings.StrataDir)) d.SelectedPath = settings.StrataDir;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!StrataInstall.IsValid(d.SelectedPath))
                {
                    MessageBox.Show(this, "That folder does not look like a Strata install.\r\nIt should contain serve\\server.py and .venv\\Scripts\\python.exe.\r\nRun Strata's own START-HERE.bat once to set it up.",
                        "StrataHome", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                settings.StrataDir = d.SelectedPath;
                settings.Save();
                LoadInstall();
                RefreshUi();
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
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --minimized");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------ actions

        ModelEntry Selected() { return cmbModel.SelectedItem as ModelEntry; }

        void StartServer()
        {
            SaveFromUi();
            ModelEntry m = Selected();
            string mode = settings.Mode; int idle = settings.IdleMinutes;
            ThreadPool.QueueUserWorkItem(delegate { launcher.Start(m, mode, idle); });
        }

        void StopServer()
        {
            ThreadPool.QueueUserWorkItem(delegate { launcher.Stop(); });
        }

        void RestartServer()
        {
            SaveFromUi();
            ModelEntry m = Selected();
            string mode = settings.Mode; int idle = settings.IdleMinutes;
            ThreadPool.QueueUserWorkItem(delegate { launcher.Stop(); launcher.Start(m, mode, idle); });
        }

        void GpuToggle()
        {
            bool unload = launcher.Loaded;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string r = unload ? launcher.Unload() : launcher.Load();
                pendingLog.Enqueue("[launcher] " + (unload ? "unload" : "load") + ": " + r);
            });
        }

        void CopyUrl()
        {
            try { Clipboard.SetText(launcher.ApiUrl); tray.ShowBalloonTip(1500, "Copied", launcher.ApiUrl, ToolTipIcon.None); } catch { }
        }

        void OpenChat()
        {
            if (chat == null || chat.IsDisposed) chat = new ChatForm(launcher, settings);
            chat.Show();
            if (chat.WindowState == FormWindowState.Minimized) chat.WindowState = FormWindowState.Normal;
            chat.Activate();
        }

        // ------------------------------------------------------------------ refresh

        void PostRefresh()
        {
            if (exiting || IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(RefreshUi)); } catch { }
        }

        static string Title(RunState s, bool loaded)
        {
            switch (s)
            {
                case RunState.Stopped: return "Stopped";
                case RunState.Starting: return "Starting";
                case RunState.Loading: return "Loading the model";
                case RunState.Ready: return "Ready";
                case RunState.Unloaded: return "Idle, model unloaded";
                case RunState.External: return loaded ? "Ready (started elsewhere)" : "Idle (started elsewhere)";
                case RunState.Stopping: return "Stopping";
                default: return "Problem";
            }
        }

        void RefreshUi()
        {
            RunState s = launcher.State;
            stateColor = Icons.StateColor(s);
            dot.Invalidate();
            lblState.Text = Title(s, launcher.Loaded);
            lblDetail.Text = models.Count == 0 && launcher.Dir.Length > 0
                ? "No installed model found in this Strata folder. Run START-HERE.bat once."
                : launcher.Dir.Length == 0 ? "Strata was not found. Click Change... to point at its folder." : launcher.Detail;

            ModelEntry sel = Selected();
            valModel.Text = launcher.ServedModel.Length > 0 && launcher.IsActive ? launcher.ServedModel : (sel != null ? sel.ModelName : "-");
            int ctx = launcher.IsActive && launcher.MaxContext > 0 ? launcher.MaxContext : (sel != null ? sel.MaxContext : 0);
            valContext.Text = ctx > 0 ? ctx.ToString("N0") + " tokens (" + StrataInstall.Tokens(ctx) + ")" : "-";
            valEndpoint.Text = sel != null || launcher.IsActive ? launcher.ApiUrl : "-";
            UpdateUptime();

            bool active = launcher.IsActive;
            bool busy = s == RunState.Stopping;
            btnStart.Enabled = !active && models.Count > 0;
            btnStop.Enabled = active && !busy;
            btnRestart.Enabled = active && !busy;
            bool chatOk = s == RunState.Ready || s == RunState.Unloaded || s == RunState.External;
            btnChat.Enabled = chatOk;
            btnGpu.Enabled = (s == RunState.Ready || s == RunState.Unloaded || s == RunState.External);
            btnGpu.Text = launcher.Loaded ? "Free GPU" : "Load now";
            miStart.Enabled = btnStart.Enabled; miStop.Enabled = btnStop.Enabled; miRestart.Enabled = btnRestart.Enabled;
            miChat.Enabled = chatOk; miGpu.Enabled = btnGpu.Enabled; miGpu.Text = launcher.Loaded ? "Free GPU now" : "Load model now";
            cmbModel.Enabled = !active; cmbMode.Enabled = !active; numIdle.Enabled = !active && cmbMode.SelectedIndex > 0;

            tray.Icon = Icons.For(s);
            string tip = "Strata: " + Title(s, launcher.Loaded);
            tray.Text = tip.Length > 62 ? tip.Substring(0, 62) : tip;
            if (s == RunState.Ready && previous != RunState.Ready && previous != RunState.External && launcher.LoadSeconds > 0)
                tray.ShowBalloonTip(4000, "Strata is ready", launcher.ApiUrl + "  (loaded in " + launcher.LoadSeconds.ToString("0") + " s)", ToolTipIcon.Info);
            if (s == RunState.Error && previous != RunState.Error)
                tray.ShowBalloonTip(5000, "Strata problem", launcher.Detail.Length > 120 ? launcher.Detail.Substring(0, 120) : launcher.Detail, ToolTipIcon.Warning);
            previous = s;

            if (ScreenshotPath != null && !shotScheduled && launcher.Loaded && (s == RunState.Ready || s == RunState.External))
            {
                shotScheduled = true;
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 3000;                          // let the last log lines arrive first
                t.Tick += delegate { t.Stop(); t.Dispose(); SaveScreenshot(); };
                t.Start();
            }
            if (ChatShotPath != null && !chatShotScheduled && launcher.Loaded && (s == RunState.Ready || s == RunState.External))
            {
                chatShotScheduled = true;
                System.Windows.Forms.Timer t2 = new System.Windows.Forms.Timer();
                t2.Interval = 5000;
                t2.Tick += delegate { t2.Stop(); t2.Dispose(); OpenChat(); chat.RunDemo(ChatPrompt, ChatShotPath); };
                t2.Start();
            }
        }

        void SaveScreenshot()
        {
            try
            {
                Tick();                                      // flush the log lines into the box
                // published screenshots must not carry the user's name: show a neutral home folder instead
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                logBox.Text = logBox.Text.Replace(home, @"C:\Users\you");
                lblDir.Text = lblDir.Text.Replace(home, @"C:\Users\you");
                WindowShot.Save(this, ScreenshotPath);
            }
            catch (Exception ex) { pendingLog.Enqueue("[launcher] screenshot failed: " + ex.Message); }
        }

        void UpdateUptime()
        {
            if (!launcher.IsActive) { valUptime.Text = "-"; return; }
            TimeSpan t = DateTime.UtcNow - launcher.StartedUtc;
            string up = ((int)t.TotalHours).ToString() + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
            if (launcher.State == RunState.External) valUptime.Text = "attached for " + up;
            else valUptime.Text = launcher.LoadSeconds >= 0 ? up + "   (model loaded in " + launcher.LoadSeconds.ToString("0") + " s)" : up;
        }

        void Tick()
        {
            UpdateUptime();
            string line;
            System.Text.StringBuilder sb = null;
            while (pendingLog.TryDequeue(out line))
            {
                if (sb == null) sb = new System.Text.StringBuilder();
                sb.Append(line).Append("\r\n");
            }
            if (sb != null && Visible)
            {
                logBox.AppendText(sb.ToString());
                if (logBox.TextLength > 80000) logBox.Text = logBox.Text.Substring(logBox.TextLength - 40000);
            }
        }
    }
}

