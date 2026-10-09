using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace StrataHome
{
    /// <summary>
    /// StrataHome.exe --selftest: runs the launcher's lifecycle against Strata's mock engine (no model, no GPU) and writes
    /// the result to %LOCALAPPDATA%\StrataHome\logs\selftest.txt. Exit code 0 = all passed.
    /// </summary>
    internal static class SelfTest
    {
        const int Port = 18095;

        public static int Run()
        {
            StringBuilder log = new StringBuilder();
            int fails = 0;
            Action<string, bool> check = delegate(string name, bool ok)
            {
                log.AppendLine((ok ? "PASS  " : "FAIL  ") + name);
                if (!ok) fails++;
            };
            int code;
            try
            {
                code = Body(log, check, ref fails);
            }
            catch (Exception ex)
            {
                log.AppendLine("ERROR " + ex);
                code = 3;
            }
            try
            {
                Directory.CreateDirectory(Paths.Logs);
                File.WriteAllText(Path.Combine(Paths.Logs, "selftest.txt"), log.ToString());
            }
            catch { }
            return code != 0 ? code : (fails == 0 ? 0 : 1);
        }

        static int Body(StringBuilder log, Action<string, bool> check, ref int fails)
        {
            string dir = StrataInstall.Find(Settings.Load().StrataDir);
            if (dir == null) { log.AppendLine("SKIP  no Strata folder found"); return 2; }

            ModelEntry mock = new ModelEntry();
            mock.Mock = true; mock.Port = Port; mock.ModelName = "mock";

            if (Launcher.PortOpen(Port)) { log.AppendLine("SKIP  port " + Port + " is busy"); return 2; }

            // 1. start -> ready -> stop
            Launcher l = New(dir);
            l.Start(mock, "always", 10);
            check("mock server reaches Ready", WaitFor(l, RunState.Ready, 40));
            check("health answers on the port", Launcher.Probe(Port) != null);
            string startLine = FirstLine(l);
            check("command line has no --open (no browser)", startLine.Length > 0 && startLine.IndexOf("--open", StringComparison.Ordinal) < 0);
            check("server process has no window", NoWindow(l.Pid));
            l.Stop();
            check("Stop frees the port and ends in Stopped", !Launcher.PortOpen(Port) && l.State == RunState.Stopped);
            check("Stop leaves no stray python from this run", !Alive(l.Pid));

            // 2. the app exiting (Dispose) takes the server down with it: the job object
            l = New(dir);
            l.Start(mock, "always", 10);
            WaitFor(l, RunState.Ready, 40);
            int pid = l.Pid;
            l.Dispose();
            Thread.Sleep(2000);
            check("app exit kills the server (job object)", !Launcher.PortOpen(Port) && !Alive(pid));

            // 3. KeepRunning leaves it alone, and a later launcher attaches to it
            l = New(dir); l.KeepRunning = true;
            l.Start(mock, "always", 10);
            WaitFor(l, RunState.Ready, 40);
            pid = l.Pid;
            l.Dispose();
            Thread.Sleep(1500);
            check("KeepRunning: server survives the app", Launcher.PortOpen(Port) && Alive(pid));
            Launcher l2 = New(dir);
            l2.Start(mock, "always", 10);
            check("a new app attaches to the running server (External)", WaitFor(l2, RunState.External, 10));
            l2.Stop();
            check("Stop on an attached server ends it", !Launcher.PortOpen(Port) && l2.State == RunState.Stopped);

            // 4. the port is taken by something that is not Strata
            TcpListener blocker = new TcpListener(IPAddress.Loopback, Port);
            blocker.Start();
            try
            {
                l = New(dir);
                l.Start(mock, "always", 10);
                check("busy port gives a clear error and starts nothing", l.State == RunState.Error && l.Detail.IndexOf("already used", StringComparison.Ordinal) >= 0 && l.Pid == 0);
            }
            finally { blocker.Stop(); }
            Thread.Sleep(300);

            // 5. the server dies on its own: the app notices
            l = New(dir);
            l.Start(mock, "always", 10);
            WaitFor(l, RunState.Ready, 40);
            try { Process.GetProcessById(l.Pid).Kill(); } catch { }
            check("a crashed server is reported as Error", WaitFor(l, RunState.Error, 15));
            l.Stop();

            // 6. restart works after all of that
            l = New(dir);
            l.Start(mock, "always", 10);
            check("start works again after a crash", WaitFor(l, RunState.Ready, 40));
            l.Stop();
            check("final state is clean", !Launcher.PortOpen(Port));

            // 7. shared on the network with an API key (the Server tab's Network card)
            ModelEntry mock2 = new ModelEntry();
            mock2.Mock = true; mock2.Port = NetPort; mock2.ModelName = "mock";
            if (Launcher.PortOpen(NetPort)) { log.AppendLine("SKIP  port " + NetPort + " is busy"); return 0; }
            l = New(dir);
            l.Host = "0.0.0.0";
            l.ServerKey = "sk-selftest-key";
            l.Start(mock2, "always", 10);
            check("shared server reaches Ready", WaitFor(l, RunState.Ready, 40));
            string netLine = FirstLine(l);
            check("command line has --host 0.0.0.0", netLine.IndexOf("--host 0.0.0.0", StringComparison.Ordinal) >= 0);
            check("command line has --api-key", netLine.IndexOf("--api-key", StringComparison.Ordinal) >= 0);
            check("the log never shows the key", netLine.IndexOf("sk-selftest-key", StringComparison.Ordinal) < 0 && netLine.IndexOf("redacted", StringComparison.Ordinal) >= 0);
            Health hn = Launcher.Probe(NetPort);
            check("health says a key is required", hn != null && hn.ApiKeyRequired);
            check("the LAN URLs list the port", l.NetworkUrls().Count > 0 && l.NetworkUrls()[0].EndsWith(":" + NetPort + "/v1"));
            check("a request without the key is refused", HttpCode("http://127.0.0.1:" + NetPort + "/metrics", null) == 401);
            check("a request with the key is accepted", HttpCode("http://127.0.0.1:" + NetPort + "/metrics", "sk-selftest-key") == 200);
            l.Stop();
            check("the shared server stops cleanly", !Launcher.PortOpen(NetPort) && l.State == RunState.Stopped);
            return 0;
        }

        const int NetPort = 18096;

        static int HttpCode(string url, string key)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Proxy = null;
                req.Timeout = 4000;
                req.KeepAlive = false;
                if (key != null) req.Headers["Authorization"] = "Bearer " + key;
                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse()) return (int)r.StatusCode;
            }
            catch (WebException ex)
            {
                HttpWebResponse r = ex.Response as HttpWebResponse;
                return r != null ? (int)r.StatusCode : 0;
            }
            catch { return 0; }
        }

        static Launcher New(string dir)
        {
            Launcher l = new Launcher();
            l.Dir = dir;
            return l;
        }

        static bool WaitFor(Launcher l, RunState want, int seconds)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                if (l.State == want) return true;
                Thread.Sleep(150);
            }
            return l.State == want;
        }

        static string FirstLine(Launcher l)
        {
            string[] r = l.RecentLog();
            return r.Length > 0 ? r[0] : "";
        }

        static bool NoWindow(int pid)
        {
            try { return pid > 0 && Process.GetProcessById(pid).MainWindowHandle == IntPtr.Zero; }
            catch { return false; }
        }

        static bool Alive(int pid)
        {
            if (pid <= 0) return false;
            try { return !Process.GetProcessById(pid).HasExited; }
            catch { return false; }
        }
    }
}
