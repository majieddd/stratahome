using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace StrataHome
{
    internal enum RunState { Stopped, Starting, Loading, Ready, Unloaded, External, Stopping, Error }

    internal sealed class Health
    {
        public bool Loaded;
        public int MaxContext;
        public string Model = "";
    }

    /// <summary>
    /// Starts Strata's server the way its own run script does, minus everything that costs time or needs a window:
    /// no cmd/.bat hop, no console, no browser (the run script passes --open; we do not). The process tree lives in a
    /// kill-on-close job object, and /health is polled to tell "loading" from "ready" from "unloaded".
    /// </summary>
    internal sealed class Launcher : IDisposable
    {
        public event Action Changed;               // raised on a worker thread: the owner marshals to the UI
        public event Action<string> LogLine;

        readonly object gate = new object();
        readonly List<string> ring = new List<string>();
        Process proc;
        IntPtr job = IntPtr.Zero;
        int pollGeneration;
        bool stopping;
        bool disposed;
        bool lazyMode;
        string runLogPath;

        public string Dir = "";
        public bool KeepRunning;
        public ModelEntry Model;
        public RunState State = RunState.Stopped;
        public string Detail = "";
        public bool Loaded;
        public int MaxContext;
        public string ServedModel = "";
        public DateTime StartedUtc;
        public double LoadSeconds = -1;

        public int Port { get { return Model != null ? Model.Port : 8080; } }

        public int Pid { get { try { return proc != null ? proc.Id : 0; } catch { return 0; } } }

        public string ApiUrl { get { return "http://127.0.0.1:" + Port + "/v1"; } }

        public bool IsActive
        {
            get { return State != RunState.Stopped && State != RunState.Error; }
        }

        public string[] RecentLog()
        {
            lock (ring) { return ring.ToArray(); }
        }

        // ---------------------------------------------------------------- start / stop

        public void Start(ModelEntry model, string mode, int idleMinutes)
        {
            lock (gate)
            {
                if (IsActive) return;
                Model = model;
            }
            string problem = null;
            if (model == null) problem = "No model found. Run Strata's own setup first (START-HERE.bat).";
            else if (!StrataInstall.IsValid(Dir)) problem = "The Strata folder is missing or incomplete: " + Dir;
            if (problem != null) { SetState(RunState.Error, problem); return; }

            Paths.Diag("Start(" + model.Port + ", mode " + mode + ") on thread " + Thread.CurrentThread.ManagedThreadId + "; opening run log");
            OpenLog();
            Paths.Diag("run log: " + (runLogPath ?? "(none)"));
            Loaded = false; LoadSeconds = -1; MaxContext = model.MaxContext; ServedModel = model.ModelName;
            lazyMode = mode == "ondemand";

            if (PortOpen(model.Port))
            {
                if (Probe(model.Port) != null)
                {
                    Emit("[launcher] Strata is already answering on port " + model.Port + " (started outside this app); attaching to it.");
                    StartedUtc = DateTime.UtcNow;
                    SetState(RunState.External, "Running (started outside this app)");
                    StartPoller();
                    return;
                }
                SetState(RunState.Error, "Port " + model.Port + " is already used by another program.");
                return;
            }

            StringBuilder a = new StringBuilder();
            a.Append(Q(StrataInstall.ServerPath(Dir)));
            if (model.Mock) a.Append(" --engine mock");
            else a.Append(" --engine strata --config ").Append(Q(model.ConfigPath));
            a.Append(" --port ").Append(model.Port);
            if (mode == "idle" || mode == "ondemand") a.Append(" --idle-unload ").Append(Math.Max(1, idleMinutes) * 60);
            if (mode == "ondemand") a.Append(" --lazy");             // no --open: no browser, ever

            ProcessStartInfo psi = new ProcessStartInfo(StrataInstall.PythonPath(Dir), a.ToString());
            psi.WorkingDirectory = Dir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

            Process p = new Process();
            p.StartInfo = psi;
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Emit(e.Data); };
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Emit(e.Data); };
            p.Exited += OnExited;

            Emit("[launcher] " + psi.FileName + " " + psi.Arguments);
            try
            {
                stopping = false;
                p.Start();
            }
            catch (Exception ex)
            {
                SetState(RunState.Error, "Could not start Strata: " + ex.Message);
                return;
            }
            proc = p;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (!KeepRunning)
            {
                job = Native.CreateKillOnCloseJob();
                if (job == IntPtr.Zero || !Native.AssignProcessToJobObject(job, p.Handle))
                    Emit("[launcher] warning: could not attach the job object, so Strata will not stop by itself if this app crashes.");
            }

            StartedUtc = DateTime.UtcNow;
            SetState(RunState.Starting, "Starting Strata...");
            StartPoller();
        }

        /// <summary>Stops Strata and waits until its port is free again. Call from a worker thread.</summary>
        public void Stop()
        {
            RunState before;
            lock (gate)
            {
                if (State == RunState.Stopped || State == RunState.Stopping) return;
                before = State;
                stopping = true;
                pollGeneration++;
            }
            SetState(RunState.Stopping, "Stopping...");
            int port = Port;
            try
            {
                if (before == RunState.External) KillExternal(port);
                else if (proc != null)
                {
                    if (job != IntPtr.Zero) { Native.CloseHandle(job); job = IntPtr.Zero; }   // kills the whole tree at once
                    else if (!proc.HasExited) RunHidden("taskkill", "/PID " + proc.Id + " /T /F");
                    try { proc.WaitForExit(8000); } catch { }
                }
            }
            catch (Exception ex) { Emit("[launcher] stop: " + ex.Message); }

            for (int i = 0; i < 40 && PortOpen(port); i++) Thread.Sleep(200);
            proc = null;
            Loaded = false;
            stopping = false;
            SetState(PortOpen(port) ? RunState.Error : RunState.Stopped, PortOpen(port) ? "Port " + port + " is still busy after stopping." : "Stopped");
        }

        /// <summary>Gives the GPU and RAM back now; the next request loads the model again.</summary>
        public string Unload() { return Post("/unload", 20000); }

        /// <summary>Loads the model now instead of at the first request.</summary>
        public string Load() { return Post("/load", 600000); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            pollGeneration++;
            if (!KeepRunning && proc != null && !proc.HasExited)
            {
                try
                {
                    if (job != IntPtr.Zero) { Native.CloseHandle(job); job = IntPtr.Zero; }
                    else RunHidden("taskkill", "/PID " + proc.Id + " /T /F");
                }
                catch { }
            }
        }

        // ---------------------------------------------------------------- watching

        void StartPoller()
        {
            int gen;
            lock (gate) { gen = ++pollGeneration; }
            Thread t = new Thread(delegate() { PollLoop(gen); });
            t.IsBackground = true;
            t.Name = "strata-health";
            t.Start();
        }

        void PollLoop(int gen)
        {
            int misses = 0;
            bool sawLoaded = false;
            while (gen == pollGeneration && !disposed)
            {
                Health h = Probe(Port);
                if (gen != pollGeneration) return;

                if (h == null)
                {
                    misses++;
                    if (State == RunState.External && misses >= 3)
                    {
                        Loaded = false;
                        SetState(RunState.Stopped, "The Strata server that was running has stopped.");
                        return;
                    }
                    if ((State == RunState.Ready || State == RunState.Unloaded) && misses >= 5)
                        SetState(RunState.Error, "Lost contact with the server (no answer on /health).");
                }
                else
                {
                    misses = 0;
                    MaxContext = h.MaxContext > 0 ? h.MaxContext : MaxContext;
                    if (h.Model.Length > 0) ServedModel = h.Model;
                    Loaded = h.Loaded;
                    if (h.Loaded)
                    {
                        sawLoaded = true;
                        // only a server this app started has a meaningful load time
                        if (LoadSeconds < 0 && State != RunState.External) LoadSeconds = (DateTime.UtcNow - StartedUtc).TotalSeconds;
                    }
                    RunState next;
                    string detail;
                    if (State == RunState.External)
                    {
                        next = RunState.External;
                        detail = h.Loaded ? "Running (started outside this app)" : "Running, model unloaded (started outside this app)";
                    }
                    else if (h.Loaded) { next = RunState.Ready; detail = "Serving " + ServedModel + " at " + ApiUrl; }
                    else if (sawLoaded || lazyMode) { next = RunState.Unloaded; detail = "Model unloaded: it loads again on the next request"; }
                    else { next = RunState.Loading; detail = "Loading the model..."; }
                    if (next != State || detail != Detail) SetState(next, detail);
                }
                Thread.Sleep(1000);
            }
        }

        void OnExited(object sender, EventArgs e)
        {
            if (stopping || disposed || !object.ReferenceEquals(sender, proc)) return;   // a stop, or an older process
            pollGeneration++;
            int code = -1;
            try { code = proc != null ? proc.ExitCode : -1; } catch { }
            Loaded = false;
            string last = LastMeaningfulLine();
            SetState(code == 0 ? RunState.Stopped : RunState.Error,
                     "Strata exited (code " + code + ")." + (last.Length > 0 ? " Last output: " + last : ""));
        }

        // ---------------------------------------------------------------- state + log

        void SetState(RunState s, string detail)
        {
            lock (gate) { State = s; Detail = detail; }
            Action c = Changed;
            if (c != null) c();
        }

        void Emit(string line)
        {
            lock (ring)
            {
                ring.Add(line);
                if (ring.Count > 600) ring.RemoveRange(0, 100);
            }
            if (runLogPath != null)
            {
                try { lock (ring) { File.AppendAllText(runLogPath, line + "\r\n", new UTF8Encoding(false)); } } catch { }
            }
            Action<string> h = LogLine;
            if (h != null) h(line);
            // while starting, the newest line of real output is the best progress text
            if ((State == RunState.Starting || State == RunState.Loading) && line.Trim().Length > 0 && !line.StartsWith("[launcher]"))
            {
                Detail = Trim(line, 140);
                Action c = Changed;
                if (c != null) c();
            }
        }

        string LastMeaningfulLine()
        {
            string[] r = RecentLog();
            for (int i = r.Length - 1; i >= 0; i--)
                if (r[i].Trim().Length > 0 && !r[i].StartsWith("[launcher]")) return Trim(r[i].Trim(), 160);
            return "";
        }

        void OpenLog()
        {
            // One short append per line instead of a handle held open for hours: nothing to lock, nothing to leak.
            try
            {
                Directory.CreateDirectory(Paths.Logs);
                runLogPath = Path.Combine(Paths.Logs, "strata-run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                File.AppendAllText(runLogPath, "[launcher] " + DateTime.Now.ToString("s") + " run log opened\r\n", new UTF8Encoding(false));
                // keep the five newest run logs
                string[] old = Directory.GetFiles(Paths.Logs, "strata-run-*.log");
                Array.Sort(old, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < old.Length - 5; i++) { try { File.Delete(old[i]); } catch { } }
            }
            catch (Exception ex)
            {
                runLogPath = null;
                Paths.Diag("run log NOT opened (" + Paths.Logs + "): " + ex.Message);
                lock (ring) { ring.Add("[launcher] could not open the log file: " + ex.Message); }
            }
        }

        // ---------------------------------------------------------------- helpers

        static string Trim(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        static string Q(string s) { return "\"" + s + "\""; }

        public static bool PortOpen(int port)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect("127.0.0.1", port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(400)) return false;
                    c.EndConnect(ar);
                    return c.Connected;
                }
            }
            catch { return false; }
        }

        public static Health Probe(int port)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/health");
                req.Proxy = null;
                req.Timeout = 3000;
                req.KeepAlive = false;
                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                {
                    Dictionary<string, object> d = new JavaScriptSerializer().DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>;
                    if (d == null) return null;
                    object v;
                    if (!d.TryGetValue("service", out v) || !"strata".Equals(v as string)) return null;
                    Health h = new Health();
                    if (d.TryGetValue("loaded", out v) && v is bool) h.Loaded = (bool)v;
                    if (d.TryGetValue("max_context", out v) && v != null) h.MaxContext = Convert.ToInt32(v);
                    if (d.TryGetValue("model", out v) && v is string) h.Model = (string)v;
                    return h;
                }
            }
            catch { return null; }
        }

        string Post(string path, int timeoutMs)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + Port + path);
                req.Method = "POST";
                req.Proxy = null;
                req.Timeout = timeoutMs;
                req.ContentLength = 0;
                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                if (ex.Response != null)
                {
                    try { using (StreamReader sr = new StreamReader(ex.Response.GetResponseStream())) return sr.ReadToEnd(); }
                    catch { }
                }
                return ex.Message;
            }
            catch (Exception ex) { return ex.Message; }
        }

        static void RunHidden(string file, string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, args);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            using (Process p = Process.Start(psi)) { p.WaitForExit(8000); }
        }

        /// <summary>Stops a Strata server this app did not start: finds the python running serve\server.py on the port.</summary>
        void KillExternal(int port)
        {
            using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'python.exe'"))
            {
                foreach (ManagementObject o in s.Get())
                {
                    string cl = Convert.ToString(o["CommandLine"]);
                    if (cl.IndexOf("server.py", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    bool samePort = cl.IndexOf("--port " + port, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool defaultPort = port == 8095 && cl.IndexOf("--port", StringComparison.OrdinalIgnoreCase) < 0;
                    if (!samePort && !defaultPort) continue;
                    Emit("[launcher] stopping external Strata server (pid " + o["ProcessId"] + ")");
                    RunHidden("taskkill", "/PID " + o["ProcessId"] + " /T /F");
                }
            }
        }
    }
}
