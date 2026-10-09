using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Markup;

[assembly: AssemblyTitle("StrataHome")]
[assembly: AssemblyDescription("An unofficial tray launcher for Strata, with its own window in the look of the Strata web app.")]
[assembly: AssemblyProduct("StrataHome")]
[assembly: AssemblyVersion("0.3.2.0")]
[assembly: AssemblyFileVersion("0.3.2.0")]

namespace StrataHome
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (Has(args, "--probe")) return Probe();
            if (Has(args, "--updater-selftest")) return UpdaterTest.Run();
            if (Has(args, "--selftest")) return SelfTest.Run();

            Options opt = new Options();
            opt.Minimized = Has(args, "--minimized");
            opt.NoStart = Has(args, "--no-start");
            opt.Drawer = Has(args, "--drawer");
            opt.RestartTest = Has(args, "--uitest-restart");
            opt.UiSmoke = Has(args, "--uitest-ui");
            opt.UiTest = Has(args, "--uitest") || opt.RestartTest || opt.UiSmoke;
            string size = ValueOf(args, "--size");                       // e.g. 900x700 (for the layout checks)
            if (size != null && size.Contains("x"))
            {
                double sw, sh;
                string[] p = size.Split('x');
                if (double.TryParse(p[0], out sw) && double.TryParse(p[1], out sh)) { opt.WidthPx = sw; opt.HeightPx = sh; }
            }
            opt.Mode = ValueOf(args, "--mode");
            int.TryParse(ValueOf(args, "--idle-minutes"), out opt.IdleMinutes);
            opt.ScreenshotPath = ValueOf(args, "--screenshot");
            opt.Tab = ValueOf(args, "--tab");
            opt.ThemeName = ValueOf(args, "--theme");
            opt.Prompt = ValueOf(args, "--prompt");
            double.TryParse(ValueOf(args, "--scroll"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out opt.ScrollY);
            double delay;
            if (double.TryParse(ValueOf(args, "--delay"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out delay)) opt.DelaySeconds = delay;

            // --instance <name> gives a second copy its own single-instance lock (used by the tests next to a running app)
            string inst = ValueOf(args, "--instance");
            string suffix = string.IsNullOrEmpty(inst) ? "" : "." + inst;
            bool first;
            using (Mutex single = new Mutex(true, @"Local\StrataHome.Launcher" + suffix, out first))
            using (EventWaitHandle showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\StrataHome.Show" + suffix))
            {
                if (!first) { showSignal.Set(); return 0; }       // already running: just bring it forward

                Paths.Diag("start: " + System.Windows.Forms.Application.ExecutablePath + " " + string.Join(" ", args) + " | user " + Environment.UserName +
                           " | logs " + Paths.Logs + " | settings " + Paths.Roaming);
                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                app.DispatcherUnhandledException += delegate(object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) { LogCrash(e.Exception); e.Handled = true; };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { LogCrash(e.ExceptionObject as Exception); };

                Fonts.Init();
                Settings pre = Settings.Load();
                string wanted = opt.ThemeName ?? pre.Theme;
                bool dark = wanted == "dark" || (wanted != "light" && SystemUsesDark());
                Theme.Apply(dark);
                app.Resources.MergedDictionaries.Add(LoadStyles());

                MainWindow win = new MainWindow(opt);
                win.SetTheme(dark, false);

                Thread waiter = new Thread(delegate()
                {
                    while (showSignal.WaitOne())
                    {
                        try { app.Dispatcher.BeginInvoke(new Action(win.ShowFromTray)); } catch { return; }
                    }
                });
                waiter.IsBackground = true;
                waiter.Start();

                if (!opt.Minimized) win.Show();
                return app.Run();
            }
        }

        static ResourceDictionary LoadStyles()
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Styles.xaml"))
                return (ResourceDictionary)XamlReader.Load(s);
        }

        static bool SystemUsesDark()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
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
