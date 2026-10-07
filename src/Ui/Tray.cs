using System;
using System.Diagnostics;
using System.IO;

namespace StrataHome
{
    /// <summary>The notification-area icon and its menu. Uses Windows Forms' NotifyIcon (WPF has none); everything else is WPF.</summary>
    internal sealed class Tray : IDisposable
    {
        readonly System.Windows.Forms.NotifyIcon icon = new System.Windows.Forms.NotifyIcon();
        readonly MainWindow w;
        System.Windows.Forms.ToolStripMenuItem miStart, miStop, miRestart, miChat, miGpu;
        RunState last = RunState.Stopped;

        public Tray(MainWindow window)
        {
            w = window;
            System.Windows.Forms.ContextMenuStrip m = new System.Windows.Forms.ContextMenuStrip();
            System.Windows.Forms.ToolStripMenuItem show = new System.Windows.Forms.ToolStripMenuItem("Show StrataHome");
            show.Font = new System.Drawing.Font(show.Font, System.Drawing.FontStyle.Bold);
            show.Click += delegate { w.ShowFromTray(); };
            miStart = new System.Windows.Forms.ToolStripMenuItem("Start Strata"); miStart.Click += delegate { w.StartServer(); };
            miStop = new System.Windows.Forms.ToolStripMenuItem("Stop Strata"); miStop.Click += delegate { w.StopServer(); };
            miRestart = new System.Windows.Forms.ToolStripMenuItem("Restart"); miRestart.Click += delegate { w.RestartServer(); };
            miChat = new System.Windows.Forms.ToolStripMenuItem("Chat"); miChat.Click += delegate { w.ShowTab("chat"); w.ShowFromTray(); };
            System.Windows.Forms.ToolStripMenuItem mon = new System.Windows.Forms.ToolStripMenuItem("Monitor"); mon.Click += delegate { w.ShowTab("monitor"); w.ShowFromTray(); };
            System.Windows.Forms.ToolStripMenuItem copy = new System.Windows.Forms.ToolStripMenuItem("Copy API URL"); copy.Click += delegate { w.CopyUrl(); };
            miGpu = new System.Windows.Forms.ToolStripMenuItem("Free GPU now"); miGpu.Click += delegate { w.GpuToggle(); };
            System.Windows.Forms.ToolStripMenuItem logs = new System.Windows.Forms.ToolStripMenuItem("Open log folder");
            logs.Click += delegate { try { Directory.CreateDirectory(Paths.Logs); Process.Start("explorer.exe", Paths.Logs); } catch { } };
            System.Windows.Forms.ToolStripMenuItem exit = new System.Windows.Forms.ToolStripMenuItem("Exit");
            exit.Click += delegate { w.ExitApp(); };
            m.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
                show, new System.Windows.Forms.ToolStripSeparator(), miStart, miStop, miRestart,
                new System.Windows.Forms.ToolStripSeparator(), miChat, mon, copy, miGpu,
                new System.Windows.Forms.ToolStripSeparator(), logs, exit });
            icon.ContextMenuStrip = m;
            icon.Icon = Icons.For(RunState.Stopped);
            icon.Text = "StrataHome";
            icon.Visible = true;
            icon.DoubleClick += delegate { w.ShowFromTray(); };
            icon.BalloonTipClicked += delegate { w.ShowFromTray(); };
        }

        public void Update(Launcher l, string title)
        {
            RunState s = l.State;
            bool active = l.IsActive;
            bool chatOk = s == RunState.Ready || s == RunState.Unloaded || s == RunState.External;
            miStart.Enabled = !active;
            miStop.Enabled = active && s != RunState.Stopping;
            miRestart.Enabled = miStop.Enabled;
            miChat.Enabled = true;
            miGpu.Enabled = chatOk;
            miGpu.Text = l.Loaded ? "Free GPU now" : "Load model now";
            icon.Icon = Icons.For(s);
            string tip = "Strata: " + title;
            icon.Text = tip.Length > 62 ? tip.Substring(0, 62) : tip;
            if (s == RunState.Ready && last != RunState.Ready && last != RunState.External && l.LoadSeconds > 0)
                Balloon("Strata is ready", l.ApiUrl + "  (loaded in " + l.LoadSeconds.ToString("0") + " s)", false);
            if (s == RunState.Error && last != RunState.Error)
                Balloon("Strata problem", l.Detail.Length > 120 ? l.Detail.Substring(0, 120) : l.Detail, true);
            last = s;
        }

        public void Balloon(string title, string text, bool warn)
        {
            icon.ShowBalloonTip(warn ? 5000 : 4000, title, text, warn ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
        }
    }
}
