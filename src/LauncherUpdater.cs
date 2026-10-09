using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace StrataHome
{
    /// <summary>
    /// Updates the launcher itself from the GitHub release, the same way StrataUpdater updates Strata:
    /// check the release, download the asset, verify its SHA-256 against the digest GitHub publishes,
    /// keep a copy of the running exe, and swap the file on a clean quit (the running exe cannot be
    /// replaced while it is running). A pending swap left by a crash is applied at the next startup.
    /// </summary>
    internal static class LauncherUpdater
    {
        public const string Repo = "majieddd/stratahome";

        public static string ExePath
        {
            get
            {
                if (!string.IsNullOrEmpty(ExePathOverride)) return ExePathOverride;
                string path = System.Reflection.Assembly.GetExecutingAssembly().Location;
                return string.IsNullOrEmpty(path) ? Process.GetCurrentProcess().MainModule.FileName : path;
            }
        }

        /// <summary>Test hook: point the swap at a file that is not the running exe, so the swap can be driven for real.</summary>
        public static string ExePathOverride = "";

        public static string UpdatesDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StrataHome", "updates"); } }
        public static string PendingFile { get { return Path.Combine(UpdatesDir, "launcher-pending.txt"); } }

        /// <summary>The version this exe is, from the assembly (the same number About shows).</summary>
        public static string CurrentVersion
        {
            get
            {
                Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v.Revision > 0 ? v.Major + "." + v.Minor + "." + v.Build + "." + v.Revision : v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        /// <summary>"v0.3.2" / "0.3.2" -> "0.3.2", so a release tag compares cleanly against the assembly version.</summary>
        public static string Normalize(string tag)
        {
            string t = (tag ?? "").Trim();
            if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase)) t = t.Substring(1);
            return t;
        }

        /// <summary>0 when equal, 1 when a is newer, -1 when older. Missing parts count as 0, so 0.3.2 beats 0.3.1.9.</summary>
        public static int Compare(string a, string b)
        {
            string[] x = Normalize(a).Split('.'), y = Normalize(b).Split('.');
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                int p = i < x.Length ? Num(x[i]) : 0, q = i < y.Length ? Num(y[i]) : 0;
                if (p != q) return p > q ? 1 : -1;
            }
            return 0;
        }

        static int Num(string s)
        {
            int n;
            return int.TryParse(s, out n) ? n : 0;
        }

        /// <summary>What the release carries. Null when the network is unreachable.</summary>
        public static LauncherReleaseInfo Check()
        {
            try
            {
                string json = GetJson("https://api.github.com/repos/" + Repo + "/releases/latest");
                Dictionary<string, object> r = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                if (r == null) return null;
                LauncherReleaseInfo info = new LauncherReleaseInfo();
                info.tag = Convert.ToString(r["tag_name"]);
                info.name = Convert.ToString(r["name"]);
                info.body = Convert.ToString(r["body"]);
                System.Collections.IList assets = r["assets"] as System.Collections.IList;
                if (assets == null) return null;
                foreach (object o in assets)
                {
                    Dictionary<string, object> a = o as Dictionary<string, object>;
                    if (a == null) continue;
                    string name = Convert.ToString(a["name"]);
                    if (!name.Equals("StrataHome.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    info.url = Convert.ToString(a["browser_download_url"]);
                    string digest = Convert.ToString(a["digest"]);
                    if (digest.StartsWith("sha256:")) info.sha256 = digest.Substring(7).ToLower();
                    break;
                }
                if (string.IsNullOrEmpty(info.url)) return null;
                return info;
            }
            catch (Exception ex) { Paths.Diag("launcher check failed: " + ex.Message); return null; }
        }

        static string GetJson(string url)
        {
            // GitHub requires TLS 1.2; .NET Framework defaults to older versions
            ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "StrataHome/" + CurrentVersion;
            req.Accept = "application/vnd.github+json";
            req.Proxy = null;
            req.Timeout = 15000;
            req.KeepAlive = false;
            using (WebResponse response = req.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                return reader.ReadToEnd();
        }

        /// <summary>Download the release exe and verify its SHA-256. Returns the staged file, or null with a message.</summary>
        public static string Download(LauncherReleaseInfo info, out string message)
        {
            message = "";
            if (info == null || string.IsNullOrEmpty(info.url)) { message = "The release has no StrataHome.exe to download."; return null; }
            string dir = Path.Combine(UpdatesDir, "launcher-" + Normalize(info.tag));
            string target = Path.Combine(dir, "StrataHome.exe");
            try
            {
                Directory.CreateDirectory(dir);
                using (WebClient wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "StrataHome/" + CurrentVersion;
                    wc.DownloadFile(info.url, target);
                }
                string got = Sha256(target);
                if (string.IsNullOrEmpty(info.sha256))
                {
                    message = "The release does not publish a SHA-256 for StrataHome.exe, so the download cannot be verified. Nothing was staged.";
                    return null;
                }
                if (!got.Equals(info.sha256, StringComparison.OrdinalIgnoreCase))
                {
                    message = "The download's SHA-256 is " + got + ", the release says " + info.sha256 + ". Nothing was staged.";
                    File.Delete(target);
                    return null;
                }
                File.WriteAllText(PendingFile, target + "\n" + Normalize(info.tag));
                return target;
            }
            catch (Exception ex)
            {
                message = "The download failed: " + ex.Message;
                return null;
            }
        }

        public static string Sha256(string path)
        {
            using (SHA256Managed sha = new SHA256Managed())
            using (FileStream fs = File.OpenRead(path))
            {
                StringBuilder sb = new StringBuilder();
                foreach (byte b in sha.ComputeHash(fs)) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Swap the staged exe in for the running one: the running file is copied to
        /// updates\launcher-before-&lt;tag&gt; first, so a bad build can be put back by hand.
        /// </summary>
        public static bool ApplyPending(out string message) { return ApplyPending(out message, ExePath); }

        public static bool ApplyPending(out string message, string exe)
        {
            message = "";
            if (!File.Exists(PendingFile)) return false;
            string[] lines = File.ReadAllLines(PendingFile);
            if (lines.Length < 2) { File.Delete(PendingFile); return false; }
            string staged = lines[0].Trim(), tag = lines[1].Trim();
            if (!File.Exists(staged)) { message = "The staged launcher is gone: " + staged; File.Delete(PendingFile); return false; }
            string backupDir = Path.Combine(UpdatesDir, "launcher-before-" + tag);
            try
            {
                Directory.CreateDirectory(backupDir);
                File.Copy(exe, Path.Combine(backupDir, "StrataHome.exe"), true);
                File.Copy(staged, exe, true);
                File.Delete(PendingFile);
                message = "StrataHome " + tag + " is installed. The version you had is kept in " + backupDir + ".";
                return true;
            }
            catch (Exception ex)
            {
                message = "The swap failed: " + ex.Message + ". The staged file is still at " + staged + ".";
                return false;
            }
        }

        /// <summary>
        /// A running exe cannot be replaced, so the swap runs in a detached helper: it waits for this app to close
        /// (the marker file is deleted on exit), keeps a copy of the version being replaced, copies the staged file
        /// in, and starts the new one.
        /// </summary>
        public static string SpawnSwap(string staged, string exe, string tag, string marker)
        {
            string script = Path.Combine(Path.GetTempPath(), "stratahome-swap-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".cmd");
            string backup = Path.Combine(UpdatesDir, "launcher-before-" + tag);
            List<string> lines = new List<string>();
            lines.Add("@echo off");
            lines.Add(":loop");
            lines.Add("if exist \"" + marker + "\" (ping -n 2 127.0.0.1 >nul & goto loop)");
            lines.Add("ping -n 4 127.0.0.1 >nul");   // the app is closing: give it a moment to release the file
            lines.Add("if not exist \"" + staged + "\" exit /b 1");
            lines.Add("mkdir \"" + backup + "\" 2>nul");
            lines.Add("copy /y \"" + exe + "\" \"" + backup + "\\StrataHome.exe\" >nul");
            lines.Add("copy /y \"" + staged + "\" \"" + exe + "\" >nul");
            lines.Add("del \"" + PendingFile + "\"");
            lines.Add("start \"\" \"" + exe + "\"");
            lines.Add("del \"%~f0\"");
            File.WriteAllText(script, string.Join("\r\n", lines.ToArray()) + "\r\n", new UTF8Encoding(false));
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi);
            return script;
        }

        /// <summary>Apply a swap left by a crash between the download and the quit. Called at startup, before the window.</summary>
        public static bool ApplyPendingAtStartup()
        {
            if (!File.Exists(PendingFile)) return false;
            string message;
            bool ok = ApplyPending(out message);
            Paths.Diag("startup launcher swap: " + (ok ? "applied - " + message : message));
            return ok;
        }
    }

    /// <summary>
    /// Drives the launcher's own update from the window: check the release, compare against this exe's version,
    /// stage the download, and swap the file in when the app quits (a running exe cannot replace itself).
    /// </summary>
    internal sealed class LauncherSelfUpdater
    {
        readonly MainWindow w;
        DateTime nextCheck = DateTime.UtcNow.AddSeconds(20);
        public bool Busy, Checking, Available, Staged;
        public string Latest = "", Status = "No launcher update check yet.";

        /// <summary>Test hook: the release the check returns, so the flow runs with no network.</summary>
        internal Func<LauncherReleaseInfo> CheckRelease;

        public LauncherSelfUpdater(MainWindow window) { w = window; }

        void Post(Action action) { w.Dispatcher.BeginInvoke(action); }
        void Log(string line) { Paths.Diag("[launcher-update] " + line); Post(delegate { w.Server.AppendLog("[launcher-update] " + line + "\r\n"); }); }

        public void CheckNow() { Check(true); }

        public void Tick()
        {
            if (Checking || Busy || w.Opt.UiTest) return;
            if (w.Settings.AutoUpdateLauncher && DateTime.UtcNow >= nextCheck) { Check(false); return; }
            if (Staged && w.Launcher.State != RunState.Stopped) return;   // never swap while Strata is mid-request
        }

        void Check(bool notify)
        {
            if (Checking || Busy) return;
            nextCheck = DateTime.UtcNow.AddHours(6);
            Checking = true;
            Status = "Checking StrataHome releases...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                LauncherReleaseInfo info = CheckRelease != null ? CheckRelease() : LauncherUpdater.Check();
                Post(delegate
                {
                    Checking = false;
                    if (info == null) { Status = "Could not check StrataHome releases (no network, or the release page is unreachable)."; if (notify) w.Toast("warn", "Launcher check failed", Status, 7000); return; }
                    Latest = LauncherUpdater.Normalize(info.tag);
                    string current = LauncherUpdater.CurrentVersion;
                    Available = LauncherUpdater.Compare(Latest, current) > 0;
                    Status = Available ? "StrataHome " + Latest + " is available (you have " + current + "). Update now downloads and verifies it; the new file is put in when this app quits."
                                       : "StrataHome is up to date. Installed: " + current + ". Latest: " + Latest + ".";
                    Log(Status);
                    if (notify) w.Toast("success", Available ? "StrataHome update available" : "StrataHome is up to date", Status, 6000);
                });
            });
        }

        /// <summary>Download, verify, stage. The file goes in on the next quit, so nothing is installed mid-request.</summary>
        public void UpdateNow()
        {
            if (Busy || Checking) return;
            if (!Available) { Check(true); return; }
            Busy = true;
            Status = "Downloading StrataHome " + Latest + "...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string message;
                LauncherReleaseInfo info = CheckRelease != null ? CheckRelease() : LauncherUpdater.Check();
                string staged = LauncherUpdater.Download(info, out message);
                Post(delegate
                {
                    Busy = false;
                    if (staged == null) { Status = message; Log(message); w.Toast("warn", "Launcher download refused", message, 9000); return; }
                    Staged = true;
                    Status = "StrataHome " + Latest + " is downloaded and verified. It is put in when this app quits - use Exit. The version you had is kept as a recovery copy.";
                    Log(Status);
                    w.Toast("success", "StrataHome " + Latest + " staged", "Use Exit. The new version starts when this app closes.", 9000);
                });
            });
        }

        /// <summary>Called from ExitApp: write the exit marker and hand the swap to a detached helper. The marker is
        /// deleted at the end of ExitApp; the helper sees it gone, waits a moment, then swaps and restarts.</summary>
        public bool SwapOnExit()
        {
            if (!Staged) return false;
            string exe = LauncherUpdater.ExePath;
            string staged = File.ReadAllLines(LauncherUpdater.PendingFile)[0].Trim();
            string marker = Path.Combine(Path.GetTempPath(), "stratahome-exit-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".marker");
            try { File.WriteAllText(marker, "closing"); } catch { return false; }
            string script = LauncherUpdater.SpawnSwap(staged, exe, Latest, marker);
            Log("swap handed to " + script + ", marker " + marker);
            ExitMarker = marker;
            return true;
        }

        public string ExitMarker = "";
    }

    internal sealed class LauncherReleaseInfo
    {
        public string tag = "", name = "", body = "", url = "", sha256 = "";

        public string Notes()
        {
            string b = (body ?? "").Replace("\r", "");
            List<string> keep = new List<string>();
            foreach (string line in b.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("<!--")) continue;
                t = t.Replace("**", "").Replace("`", "");
                if (t.StartsWith("- ")) keep.Add(t.Substring(2));
                else if (t.Length > 1 && t[0] == '#' && t[1] == '#') keep.Add(t.TrimStart('#', ' '));
            }
            return string.Join("\n", keep.ToArray());
        }
    }
}
