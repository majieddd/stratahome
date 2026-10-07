using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("StrataHome")]
[assembly: AssemblyDescription("An unofficial tray launcher for Strata: starts the local server without a console or a browser.")]
[assembly: AssemblyProduct("StrataHome")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]

namespace StrataHome
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool minimized = Has(args, "--minimized");
            bool noStart = Has(args, "--no-start");

            if (Has(args, "--probe")) return Probe();
            if (Has(args, "--selftest")) return SelfTest.Run();

            bool first;
            using (Mutex single = new Mutex(true, @"Local\StrataHome.Launcher", out first))
            using (EventWaitHandle showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\StrataHome.Show"))
            {
                if (!first) { showSignal.Set(); return 0; }       // already running: just bring it forward

                Paths.Diag("start: " + Application.ExecutablePath + " " + string.Join(" ", args) + " | user " + Environment.UserName +
                           " | logs " + Paths.Logs + " | settings " + Paths.Roaming);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { LogCrash(e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { LogCrash(e.ExceptionObject as Exception); };

                int idleOverride = 0;
                int.TryParse(ValueOf(args, "--idle-minutes"), out idleOverride);
                MainForm form = new MainForm(minimized, noStart, ValueOf(args, "--mode"), idleOverride);
                form.ScreenshotPath = ValueOf(args, "--screenshot");
                form.ChatShotPath = ValueOf(args, "--screenshot-chat");
                string prompt = ValueOf(args, "--prompt");
                if (prompt != null) form.ChatPrompt = prompt;
                Thread waiter = new Thread(delegate()
                {
                    while (showSignal.WaitOne())
                    {
                        try { form.BeginInvoke(new Action(form.ShowFromTray)); } catch { return; }
                    }
                });
                waiter.IsBackground = true;
                waiter.Start();
                Application.Run(form);
            }
            return 0;
        }

        static bool Has(string[] args, string flag)
        {
            foreach (string a in args) if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string ValueOf(string[] args, string flag)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        /// <summary>--probe: write what the app would use (Strata folder, models) to a text file and exit. For checking installs.</summary>
        static int Probe()
        {
            Settings s = Settings.Load();
            string dir = StrataInstall.Find(s.StrataDir);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("strata folder: " + (dir ?? "(not found)"));
            if (dir != null)
                foreach (ModelEntry m in StrataInstall.Models(dir))
                    sb.AppendLine("model: " + m.Label + "  port " + m.Port + "  " + m.FileName);
            Directory.CreateDirectory(Paths.Logs);
            File.WriteAllText(Path.Combine(Paths.Logs, "probe.txt"), sb.ToString());
            return dir != null ? 0 : 2;
        }

        static void LogCrash(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Paths.Logs);
                File.AppendAllText(Path.Combine(Paths.Logs, "crash.log"), DateTime.Now.ToString("s") + "  " + ex + "\r\n\r\n");
            }
            catch { }
        }
    }
}
