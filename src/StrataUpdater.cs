using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace StrataHome
{
    // All policy decisions run on the window thread. File/network work runs on workers.
    internal sealed class StrataUpdater
    {
        readonly MainWindow w;
        DateTime nextCheck = DateTime.UtcNow.AddSeconds(15), idleSince = DateTime.MinValue;
        string checkedDir = "", failedTag = "";
        bool requested;
        internal Func<string, string> CheckRelease;
        public bool Busy, Checking, Available;
        public bool ManualRequested { get { return requested; } }
        public string Latest = "", Status = "No update check yet.";
        public StrataUpdater(MainWindow window) { w = window; CheckRelease = delegate(string dir) { return RunHelper(dir, "--check", false); }; }

        void Post(Action action) { w.Dispatcher.BeginInvoke(action); }
        void Changed() { w.Server.RefreshState(); w.Chat.RefreshState(); w.About.Root.IsEnabled = !Busy; w.Drawer.Root.IsEnabled = !Busy; }
        void Log(string line) { Paths.Diag("[update] " + line); Post(delegate { w.Server.AppendLog("[update] " + line + "\r\n"); }); }

        public static bool IdleMetrics(object metrics, DateTime now)
        {
            double? stamp = J.Dbl(metrics, "time"), queued = J.Dbl(metrics, "live", "queued");
            string state = J.Str(metrics, "live", "state");
            if (!stamp.HasValue || queued != 0 || (state != "idle" && state != "unloaded")) return false;
            double age = (now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds - stamp.Value;
            return age >= -5 && age < 12;
        }

        public void Tick()
        {
            if (Busy || Checking || w.Opt.UiTest) return;
            if (checkedDir != w.Launcher.Dir) { Available = false; Latest = ""; requested = false; failedTag = ""; checkedDir = w.Launcher.Dir; nextCheck = DateTime.UtcNow.AddSeconds(15); idleSince = DateTime.MinValue; }
            if (w.Settings.AutoUpdateStrata && DateTime.UtcNow >= nextCheck) { Check(false, false); return; }
            if (!Available || (!requested && (!w.Settings.AutoUpdateStrata || failedTag == Latest))) return;
            RunState s = w.Launcher.State;
            bool stopped = s == RunState.Stopped || s == RunState.Error;
            bool ready = s == RunState.Ready || s == RunState.Unloaded || (requested && s == RunState.External);
            bool idle = !w.Chat.Busy && (stopped || (ready && IdleMetrics(w.Metrics.Last, DateTime.UtcNow)));
            if (!idle) { idleSince = DateTime.MinValue; return; }
            if (idleSince == DateTime.MinValue) idleSince = DateTime.UtcNow;
            if (!IdleDelaySatisfied(requested, (DateTime.UtcNow - idleSince).TotalSeconds)) return;
            Apply(stopped);
        }

        public static bool IdleDelaySatisfied(bool manual, double idleSeconds) { return manual || idleSeconds >= 30; }

        public void CheckNow() { Check(false, true); }

        void Check(bool updateAfterCheck, bool notify)
        {
            if (Checking || Busy) return;
            nextCheck = DateTime.UtcNow.AddHours(6);
            string dir = w.Launcher.Dir;
            if (!StrataInstall.IsValid(dir)) { Status = "Choose an installed Strata folder first."; Changed(); return; }
            checkedDir = dir; Checking = true; Status = "Checking official Strata releases..."; Changed();
            nextCheck = DateTime.UtcNow.AddHours(6);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string output = CheckRelease(dir);
                    object result = new JavaScriptSerializer().DeserializeObject(output);
                    Post(delegate
                    {
                        Checking = false;
                        if (dir != w.Launcher.Dir) return;
                        Latest = J.Str(result, "latest") ?? "";
                        Available = J.Bool(result, "available");
                        idleSince = DateTime.MinValue;
                        requested = Available && (requested || updateAfterCheck);
                        Status = Available ? "Strata " + Latest + " available. " + (requested ? "Waiting for Strata to be idle." : "Automatic updates wait for 30 seconds without requests. Select Update now to install when idle.")
                                           : "Strata is up to date. Installed: " + J.Str(result, "installed") + ". Latest: " + Latest + ".";
                        Log(Status);
                        Changed();
                        if (notify && !requested) w.Toast("success", Available ? "Strata update available" : "Strata is up to date", Status, 6000);
                    });
                }
                catch (Exception ex) { Log("Could not check for updates: " + ex.Message); Post(delegate { Checking = false; Available = false; Status = "Could not check for updates: " + ex.Message; Changed(); if (notify) w.Toast("warn", "Update check failed", Status, 7000); }); }
            });
        }

        public void UpdateNow()
        {
            if (Busy || Checking || requested) return;
            if (!Available) { Check(true, true); return; }
            requested = true;
            Status = "Update queued. Waiting for Strata to be idle.";
            Log(Status); Changed();
        }

        object FreshMetrics(int port, string key)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/metrics");
            req.Proxy = null; req.Timeout = 5000; req.KeepAlive = false;
            if (!string.IsNullOrEmpty(key)) req.Headers["Authorization"] = "Bearer " + key;
            using (WebResponse response = req.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(reader.ReadToEnd());
        }

        void Apply(bool stopped)
        {
            string dir = w.Launcher.Dir, tag = Latest, mode = w.Settings.Mode, key = w.Settings.ApiKey;
            int idle = w.Settings.IdleMinutes;
            ModelEntry model = w.SelectedModel;
            if (model == null) { requested = false; Status = "Select an installed model before updating."; Changed(); return; }
            Busy = true; requested = false; Status = "Updating Strata " + tag + "..."; Changed();
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool changed = false, stopAttempted = false;
                try
                {
                    // Recheck from the server, not a cached UI sample, immediately before stopping it.
                    if (!stopped && !IdleMetrics(FreshMetrics(model.Port, key), DateTime.UtcNow))
                    {
                        Post(delegate { Busy = false; idleSince = DateTime.MinValue; requested = true; Status = "Update waiting for requests to finish."; Changed(); });
                        return;
                    }
                    if (stopped && Launcher.PortOpen(model.Port)) throw new Exception("A server is using the model port. Attach to it before updating.");
                    if (!stopped) { stopAttempted = true; w.Launcher.Stop(); }
                    if (Launcher.PortOpen(model.Port)) throw new Exception("Strata did not stop; no files were changed.");
                    RunHelper(dir, "--apply " + Quote(tag) + " --backup-dir " + Quote(BackupDir), true);
                    changed = true;
                    if (!stopped)
                    {
                        w.Dispatcher.Invoke(new Action(delegate { w.LoadInstall(); model = w.SelectedModel; }));
                        w.Launcher.Start(model, mode, idle);
                        VerifyServer(model, mode);
                    }
                    Post(delegate { Busy = false; Available = false; Status = "Strata " + tag + " installed" + (stopped ? "." : " and running."); Changed(); w.Toast("success", "Strata updated", Status, 6000); });
                }
                catch (Exception ex)
                {
                    Log(ex.Message);
                    string recovery = "";
                    if (changed)
                    {
                        try { w.Launcher.Stop(); RunHelper(dir, "--rollback", true); recovery = " Previous engine restored."; }
                        catch (Exception restore) { recovery = " Recovery needs attention: " + restore.Message; Log(recovery); }
                    }
                    if (!stopped && stopAttempted)
                    {
                        try { w.Dispatcher.Invoke(new Action(delegate { w.LoadInstall(); model = w.SelectedModel; })); w.Launcher.Start(model, mode, idle); }
                        catch (Exception restart) { Log("Restart: " + restart.Message); }
                    }
                    string message = "Update failed: " + ex.Message + recovery + " See the Server log.";
                    Post(delegate { Busy = false; failedTag = tag; Status = message; Changed(); w.Toast("warn", "Strata update needs attention", message, 9000); });
                }
            });
        }

        void VerifyServer(ModelEntry model, string mode)
        {
            DateTime deadline = DateTime.UtcNow.AddMinutes(15);
            while (DateTime.UtcNow < deadline)
            {
                Health h = Launcher.Probe(model.Port);
                if (h != null && (h.Loaded || mode == "ondemand"))
                {
                    if (h.Model != model.ModelName || h.MaxContext != model.MaxContext) throw new Exception("The restarted server has different model or context settings");
                    return;
                }
                if (w.Launcher.State == RunState.Error) throw new Exception("Strata could not start with the new engine");
                Thread.Sleep(1000);
            }
            throw new Exception("Strata did not become ready within 15 minutes");
        }

        public static string Quote(string value)
        {
            StringBuilder result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1);
                else result.Append('\\', slashes);
                result.Append(c); slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
        static string BackupDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StrataHome", "updates", "backups"); } }
        string RunHelper(string dir, string args, bool log)
        {
            string helperDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StrataHome", "updates", "0.3.1");
            Directory.CreateDirectory(helperDir);
            string helper = Path.Combine(helperDir, "strata_update.py");
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("strata_update.py"))
            using (FileStream output = File.Create(helper)) resource.CopyTo(output);
            ProcessStartInfo psi = new ProcessStartInfo(StrataInstall.PythonPath(dir), "-u " + Quote(helper) + " --dir " + Quote(dir) + " " + args);
            psi.WorkingDirectory = dir; psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            StringBuilder stdout = new StringBuilder(), stderr = new StringBuilder();
            using (Process process = new Process { StartInfo = psi })
            {
                process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) { stdout.AppendLine(e.Data); if (log) Log(e.Data); } };
                process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) { stderr.AppendLine(e.Data); if (log) Log(e.Data); } };
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); process.WaitForExit();
                if (process.ExitCode != 0) throw new Exception(stderr.Length > 0 ? stderr.ToString().Trim() : "The update helper exited with code " + process.ExitCode);
            }
            return stdout.ToString();
        }
    }
}
