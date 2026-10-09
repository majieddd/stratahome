using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace StrataHome
{
    /// <summary>
    /// StrataHome.exe --uitest: drives the real window the way a person would (tabs, theme, drawer, chat, stop, attachment,
    /// new chat and undo) and writes PASS/FAIL lines to %LOCALAPPDATA%\StrataHome\logs\uitest.txt. Needs a running Strata.
    /// </summary>
    internal sealed class UiTest
    {
        sealed class Step
        {
            public string Name;
            public Action Do;
            public Func<bool> Until;
            public int TimeoutMs = 0;
            public Func<string> Verify;      // null = pass
        }

        readonly MainWindow w;
        readonly List<Step> steps = new List<Step>();
        readonly StringBuilder log = new StringBuilder();
        readonly DispatcherTimer timer = new DispatcherTimer();
        int index = -1, fails;
        DateTime stepStart;
        bool started;
        string tempFile;

        string cfgResult;
        bool shareWas, keyWas;

        public UiTest(MainWindow window) { w = window; }

        /// <summary>Changes the context of a sample run config three ways and checks what changed. Returns null when all is as expected.</summary>
        static string ContextRoundTrip()
        {
            string dir = Path.Combine(Path.GetTempPath(), "stratahome-cfgtest");
            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "strata-test.json");
                string sample = "{\n \"exe\": \"C:\\\\x\\\\strata.exe\",\n \"args\": [\n  \"--pack\",\n  \"p\",\n  \"--max-context\",\n  \"262144\",\n  \"--kv\",\n  \"int8\",\n  \"--kv-resident\",\n  \"32768\",\n  \"--pool-workers\",\n  \"15\"\n ],\n \"model_name\": \"t\",\n \"port\": 8080,\n \"sampling\": {\n  \"temperature\": 0.7\n },\n \"gpus_asked\": true,\n \"note\": \"caf\\u00e9\"\n}";
                File.WriteAllText(path, sample);

                string err = RunConfig.SetContext(path, 262144, 64);
                if (err != null) return "same context: " + err;
                if (File.ReadAllText(path) != sample) return "writing the same context changed the file's text (formatting differs from Python's)";

                err = RunConfig.SetContext(path, 32768, 64);
                if (err != null) return "32K: " + err;
                if (RunConfig.ArgValue(path, "--max-context") != "32768") return "--max-context is " + RunConfig.ArgValue(path, "--max-context");
                if (RunConfig.ArgValue(path, "--kv-resident") != null) return "--kv-resident was not removed below 64K";
                if (RunConfig.ArgValue(path, "--pool-workers") != "15" || RunConfig.ArgValue(path, "--kv") != "int8") return "another engine option changed";
                string text = File.ReadAllText(path);
                if (text.IndexOf("\"temperature\": 0.7") < 0 || text.IndexOf("caf\\u00e9") < 0 || text.IndexOf("\"gpus_asked\": true") < 0) return "another key changed";
                if (!File.Exists(path + ".bak") || File.ReadAllText(path + ".bak") != sample) return "the earlier file was not kept as .bak";

                err = RunConfig.SetContext(path, 131072, 64);
                if (err != null) return "128K: " + err;
                if (RunConfig.ArgValue(path, "--max-context") != "131072" || RunConfig.ArgValue(path, "--kv-resident") != "32768") return "128K did not turn KV streaming on";

                RunConfig.SetContext(path, 16384, 64);
                RunConfig.SetContext(path, 131072, 16);
                if (RunConfig.ArgValue(path, "--kv-resident") != null) return "KV streaming was turned on for a PC with 16 GB of RAM";

                string before = File.ReadAllText(path);
                if (RunConfig.SetContext(path, 999999, 64) == null) return "a context past the trained length was accepted";
                if (File.ReadAllText(path) != before) return "a refused change still touched the file";
                return null;
            }
            catch (Exception ex) { return ex.Message; }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        void Add(string name, Action act, Func<bool> until, int timeoutMs, Func<string> verify)
        {
            Step s = new Step();
            s.Name = name; s.Do = act; s.Until = until; s.TimeoutMs = timeoutMs; s.Verify = verify;
            steps.Add(s);
        }

        void Add(string name, Func<string> verify) { Add(name, null, null, 0, verify); }

        void Build()
        {
            // ---- tabs, both by code and by the tab buttons
            foreach (string t in w.TabNames)
            {
                string tab = t;
                Add("tab '" + tab + "': clicking it shows that page and only that page", delegate { w.TabButton(tab).IsChecked = true; }, null, 0, delegate
                {
                    foreach (string o in w.TabNames) if (w.IsPageVisible(o) != (o == tab)) return "page '" + o + "' visibility is wrong";
                    return null;
                });
            }
            // ---- theme
            Color before = Color.FromRgb(0, 0, 0);
            bool wasDark = Theme.Dark;
            Add("theme button flips the theme", delegate
            {
                before = ((SolidColorBrush)Theme.Get("StBg")).Color;
                w.ThemeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }, null, 0, delegate
            {
                if (Theme.Dark == wasDark) return "Theme.Dark did not change";
                if (((SolidColorBrush)Theme.Get("StBg")).Color == before) return "the background brush did not change";
                return null;
            });
            Add("theme button flips it back", delegate { w.ThemeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }, null, 0,
                delegate { return Theme.Dark == wasDark ? null : "theme did not return"; });
            // ---- sampling drawer
            Add("sampling drawer opens", delegate { w.Drawer.Open(); }, null, 0, delegate { return w.Drawer.Root.Visibility == Visibility.Visible ? null : "drawer is not visible"; });
            Add("sampling drawer closes", delegate { w.Drawer.Close(); }, delegate { return w.Drawer.Root.Visibility != Visibility.Visible; }, 2000, delegate { return null; });
            // ---- markdown
            Add("markdown: headings, lists, table, code, quote, rule, links, inline styles", delegate
            {
                string sample = "# Title\n\nSome **bold**, *italic*, `code` and a [link](https://example.com).\n\n- one\n- two\n\n1. first\n2. second\n\n> quoted\n\n---\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n```python\nprint('hi')\n```\n";
                FlowDocument d = Markdown.Build(sample);
                int lists = 0, tables = 0, ui = 0, paras = 0;
                foreach (Block b in d.Blocks) { if (b is List) lists++; else if (b is Table) tables++; else if (b is BlockUIContainer) ui++; else if (b is Paragraph) paras++; }
                md = lists + "," + tables + "," + ui + "," + paras;
            }, null, 0, delegate { return md == "2,1,2,3" ? null : "block counts (lists,tables,rule+code,paragraphs) were " + md + ", expected 2,1,2,3"; });
            // ---- monitor drawn from canned data (no server needed for this one)
            Add("monitor draws a full /metrics sample", delegate
            {
                string json = "{\"live\":{\"state\":\"generating\",\"queued\":0,\"tok_s\":111.5,\"generated\":100,\"max_tokens\":400,\"prompt_tokens\":50,\"phase\":\"answering\"},"
                    + "\"hardware\":{\"gpu_util\":97,\"gpu_mem_used\":25000000000,\"gpu_mem_total\":25651314688,\"gpu_temp\":66,\"gpu_power\":150,\"gpu_power_limit\":175,\"gpu_pcie_gen\":5,\"gpu_pcie_gen_max\":5,\"gpu_pcie_width\":16,\"gpu_pcie_rx_mb\":900,\"cpu\":55,\"ram_used\":50000000000,\"ram_total\":68000000000,\"disk_read_mb\":12.5,\"disk_write_mb\":0.4},"
                    + "\"hardware_static\":{\"gpu_name\":\"Test GPU\",\"cpu_name\":\"Test CPU\",\"cores\":8,\"threads\":16,\"psutil\":true},"
                    + "\"engine\":{\"model\":\"test\",\"max_context\":262144,\"expert_slots\":1000,\"expert_cache_mib\":8000},"
                    + "\"history\":{\"tok_s\":[0,10,50,100,111],\"gpu_util\":[1,2,90,97,97]},\"requests\":[],\"totals\":{},\"requests_kept\":0}";
                w.Monitor.Render(new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>);
            }, null, 0, delegate
            {
                if (!w.Monitor.Probe("speed").StartsWith("111.5")) return "speed tile shows '" + w.Monitor.Probe("speed") + "'";
                if (!w.Monitor.Probe("gpu").StartsWith("97")) return "gpu tile shows '" + w.Monitor.Probe("gpu") + "'";
                if (!w.Monitor.Probe("temp").StartsWith("66")) return "temp tile shows '" + w.Monitor.Probe("temp") + "'";
                if (!w.Monitor.ProbeState().StartsWith("Answering")) return "state card shows '" + w.Monitor.ProbeState() + "'";
                return null;
            });
            Add("monitor survives empty data", delegate { w.Monitor.Render(new Dictionary<string, object>()); }, null, 0, delegate { return null; });
            // ---- performance by Strata version (the Monitor card), driven from canned /metrics samples
            Add("perf: start from an empty log in a temp file", delegate
            {
                perfPath = Path.Combine(Path.GetTempPath(), "stratahome-uitest-perf.jsonl");
                try { if (File.Exists(perfPath)) File.Delete(perfPath); } catch { }
                perfRealLines = File.Exists(PerfLog.FilePath) ? CountLines(PerfLog.FilePath) : 0;
                PerfLog.PathOverride = perfPath;
                PerfLog.ResetForTest();
                w.Monitor.RenderPerf("0.1.41");
            }, null, 0, delegate { return w.Monitor.PerfRowsForTest.Count == 0 && w.Monitor.PerfNoteForTest.IndexOf("Nothing recorded") >= 0 ? null : "the card is not in the empty state"; });
            Add("perf: one version's averages come from the engine's own times", delegate
            {
                w.Monitor.Render(Sample("0.1.41",
                    "{\"time\":1000.5,\"finish\":\"stop\",\"prompt_tokens\":1000,\"reused\":0,\"prompt_ms\":200,\"output_tokens\":100,\"engine_generated\":100,\"decode_ms\":1000}," +
                    "{\"time\":1001.5,\"finish\":\"stop\",\"prompt_tokens\":500,\"reused\":0,\"prompt_ms\":100,\"output_tokens\":50,\"engine_generated\":50,\"decode_ms\":500}"));
            }, null, 0, delegate
            {
                List<string> rows = w.Monitor.PerfRowsForTest;
                string want = "0.1.41|" + Fmt.N(5000) + "|" + Fmt.N(100);
                return rows.Count == 1 && rows[0] == want ? null : "rows: " + string.Join(" / ", rows.ToArray()) + ", expected " + want;
            });
            Add("perf: a second version gets its own row, newest first", delegate
            {
                w.Monitor.Render(Sample("0.1.40", "{\"time\":900.5,\"finish\":\"stop\",\"prompt_tokens\":1000,\"reused\":0,\"prompt_ms\":250,\"output_tokens\":100,\"engine_generated\":100,\"decode_ms\":2000}"));
            }, null, 0, delegate
            {
                List<string> rows = w.Monitor.PerfRowsForTest;
                string want = "0.1.41|" + Fmt.N(5000) + "|" + Fmt.N(100) + " , 0.1.40|" + Fmt.N(4000) + "|" + Fmt.N(50);
                string got = rows.Count == 2 ? rows[0] + " , " + rows[1] : "count " + rows.Count;
                return got == want ? null : "rows: " + got + ", expected " + want;
            });
            Add("perf: the note compares the running version with the one before it", delegate { w.Monitor.RenderPerf("0.1.41"); }, null, 0, delegate
            {
                string note = w.Monitor.PerfNoteForTest;
                if (note.IndexOf(Fmt.N(4000)) < 0) return "the previous version's prefill is missing: " + note;
                if (note.IndexOf("25") < 0) return "the prefill difference (+25%) is missing: " + note;
                if (note.IndexOf("100") < 0) return "the decode difference (+100%) is missing: " + note;
                return null;
            });
            Add("perf: re-reporting the same requests records them once", delegate
            {
                w.Monitor.Render(Sample("0.1.41",
                    "{\"time\":1000.5,\"finish\":\"stop\",\"prompt_tokens\":1000,\"reused\":0,\"prompt_ms\":200,\"output_tokens\":100,\"engine_generated\":100,\"decode_ms\":1000}," +
                    "{\"time\":1001.5,\"finish\":\"stop\",\"prompt_tokens\":500,\"reused\":0,\"prompt_ms\":100,\"output_tokens\":50,\"engine_generated\":50,\"decode_ms\":500}"));
            }, null, 0, delegate
            {
                List<string> rows = w.Monitor.PerfRowsForTest;
                string want = "0.1.41|" + Fmt.N(5000) + "|" + Fmt.N(100) + " , 0.1.40|" + Fmt.N(4000) + "|" + Fmt.N(50);
                string got = rows.Count == 2 ? rows[0] + " , " + rows[1] : "count " + rows.Count;
                return got == want ? null : "the averages moved on a repeat report: " + got;
            });
            Add("perf: restore the real log file", delegate
            {
                PerfLog.ResetForTest();
                PerfLog.PathOverride = "";
                try { if (File.Exists(perfPath)) File.Delete(perfPath); } catch { }
            }, null, 0, delegate
            {
                if (PerfLog.PathOverride.Length > 0) return "the log path is not restored";
                int now = File.Exists(PerfLog.FilePath) ? CountLines(PerfLog.FilePath) : 0;
                return now == perfRealLines ? null : "the real log changed during the test: " + perfRealLines + " -> " + now;
            });
            Add("server page lists the installed models", delegate { w.Server.RefreshAll(); }, null, 0,
                delegate { return w.Server.ModelRowCount == Math.Max(1, w.Models.Count) ? null : "model rows: " + w.Server.ModelRowCount; });
            Add("server: the network card exists", delegate { return w.Server.NetworkControlsPresent ? null : "missing network controls"; });
            Add("server: sharing on shows the key row and the LAN panel", delegate
            {
                shareWas = w.Settings.ShareOnNetwork; keyWas = w.Settings.RequireKey;
                w.Server.ShareForTest(true);
                w.Server.KeyForTest(true);
                return w.Server.SharePanelVisibleForTest && w.Server.KeyRowVisibleForTest && w.Server.KeyValueForTest.StartsWith("sk-") ? null
                    : "share panel: " + w.Server.SharePanelVisibleForTest + ", key row: " + w.Server.KeyRowVisibleForTest + ", key: " + (w.Server.KeyValueForTest.Length > 0 ? "set" : "empty");
            });
            Add("server: sharing off hides the panel, and the settings are put back", delegate
            {
                w.Server.ShareForTest(false);
                bool hidden = !w.Server.SharePanelVisibleForTest;
                w.Server.ShareForTest(shareWas);
                w.Server.KeyForTest(keyWas);
                return hidden && w.Settings.ShareOnNetwork == shareWas && w.Settings.RequireKey == keyWas
                    ? null : "panel hidden: " + hidden + ", share back: " + (w.Settings.ShareOnNetwork == shareWas) + ", key back: " + (w.Settings.RequireKey == keyWas);
            });
            Add("toast appears and goes away", delegate { toasts = w.ToastCount; w.Toast("info", "uitest", "hello", 700); }, null, 0, delegate { return w.ToastCount == toasts + 1 ? null : "toast not shown"; });
            Add("toast is removed after its time", null, delegate { return w.ToastCount == toasts; }, 3000, delegate { return null; });
            // ---- the context length option (on a copy of a run config, never the real one)
            Add("run config: a context change touches only --max-context and KV streaming", delegate { cfgResult = ContextRoundTrip(); }, null, 0, delegate { return cfgResult; });
            Add("about: the context picker offers the sizes and shows the model's current one", delegate { w.ShowTab("about"); }, null, 0, delegate
            {
                ModelEntry m = w.SelectedModel;
                if (m == null || m.MaxContext <= 0) return null;               // no Strata install on this PC: nothing to show
                if (!w.About.ContextCardVisible) return "the context card is hidden";
                if (w.About.ContextPicked != m.MaxContext) return "picked " + w.About.ContextPicked + " but the config says " + m.MaxContext;
                if (w.About.ContextChipCount < RunConfig.Presets.Length) return "only " + w.About.ContextChipCount + " sizes are offered";
                return null;
            });

            // ---- the live server
            Add("server: update status, toggle and buttons exist", delegate { return w.Server.UpdateControlsPresent ? null : "missing update controls"; });
            Add("server: update locks conflicting actions", delegate
            {
                try { w.Updater.Busy = true; w.Server.RefreshState(); return w.Server.UpdateControlsLocked ? null : "a server action is enabled during an update"; }
                finally { w.Updater.Busy = false; w.Server.RefreshState(); }
            });
            Func<string, string> originalCheck = w.Updater.CheckRelease;
            int updateChecks = 0, updateToasts = 0;
            Add("server: Update now rechecks even when already current", delegate
            {
                w.Updater.CheckRelease = delegate(string dir)
                {
                    System.Threading.Thread.Sleep(150);
                    System.Threading.Interlocked.Increment(ref updateChecks);
                    return "{\"installed\":\"0.1.41\",\"latest\":\"v0.1.41\",\"available\":false}";
                };
                w.Updater.Available = false; w.Updater.Latest = "v0.1.41";
                w.Server.RefreshState(); updateToasts = w.ToastCount;
                w.Server.UpdateButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                w.Server.UpdateButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }, delegate { return !w.Updater.Checking && updateChecks > 0; }, 5000, delegate
            {
                if (updateChecks != 1) return "repeated clicks started " + updateChecks + " checks";
                if (w.Updater.Status.IndexOf("Installed: 0.1.41. Latest: v0.1.41.") < 0) return "installed/latest versions are missing";
                if (w.ToastCount <= updateToasts) return "no visible result was shown";
                return null;
            });
            Add("server: current-version button stays usable and says Up to date", delegate
            {
                return w.Server.UpdateButtonForTest.IsEnabled && (string)w.Server.UpdateButtonForTest.Content == "Up to date" ? null : "button is disabled or has an unclear label";
            });
            Add("server: queued manual update disables repeated clicks", delegate
            {
                w.Updater.Available = true;
                w.Updater.UpdateNow(); w.Updater.UpdateNow();
                return w.Updater.ManualRequested && !w.Server.UpdateButtonForTest.IsEnabled && (string)w.Server.UpdateButtonForTest.Content == "Waiting for idle..." ? null : "queued state is unclear or accepts repeated clicks";
            });
            Add("server: restore the release checker after updater UI tests", delegate { w.Updater.CheckNow(); },
                delegate { return !w.Updater.Checking; }, 5000, delegate
                {
                    w.Updater.CheckRelease = originalCheck;
                    return !w.Updater.ManualRequested && !w.Updater.Available ? null : "manual request was not cleared when no update exists";
                });
            Add("server: the StrataHome card exists", delegate { return w.Server.SelfControlsPresent ? null : "missing StrataHome update controls"; });
            bool selfWas = w.Settings.AutoUpdateLauncher;
            Func<LauncherReleaseInfo> originalSelfCheck = w.SelfUpdater.CheckRelease;
            int selfChecks = 0, selfToasts = 0;
            Add("server: StrataHome checks are not started twice by repeated clicks", delegate
            {
                w.SelfUpdater.CheckRelease = delegate
                {
                    System.Threading.Thread.Sleep(150);
                    System.Threading.Interlocked.Increment(ref selfChecks);
                    LauncherReleaseInfo info = new LauncherReleaseInfo();
                    info.tag = "v0.0.1"; info.name = "old"; info.body = "";
                    info.url = "https://example.invalid/StrataHome.exe"; info.sha256 = "";
                    return info;
                };
                w.SelfUpdater.Available = false; w.SelfUpdater.Latest = "";
                w.Server.RefreshState(); selfToasts = w.ToastCount;
                w.Server.SelfUpdateButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                w.Server.SelfUpdateButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }, delegate { return !w.SelfUpdater.Checking; }, 5000, delegate
            {
                if (selfChecks != 1) return "repeated clicks started " + selfChecks + " checks";
                if (w.SelfUpdater.Status.IndexOf("Installed: " + LauncherUpdater.CurrentVersion + ". Latest: 0.0.1.") < 0) return "installed/latest versions are missing: " + w.SelfUpdater.Status;
                if (w.ToastCount <= selfToasts) return "no visible result was shown";
                return null;
            });
            Add("server: a current version says Up to date and stays usable", delegate
            {
                w.Server.RefreshState();
                return w.Server.SelfUpdateButtonForTest.IsEnabled && (string)w.Server.SelfUpdateButtonForTest.Content == "Up to date" ? null
                    : "button is disabled or has an unclear label: " + w.Server.SelfUpdateButtonForTest.Content;
            });
            Add("server: a newer release says Update StrataHome and names both versions", delegate
            {
                w.SelfUpdater.CheckRelease = delegate
                {
                    LauncherReleaseInfo info = new LauncherReleaseInfo();
                    info.tag = "v99.0.0"; info.name = "new"; info.body = "";
                    info.url = "https://example.invalid/StrataHome.exe"; info.sha256 = "";
                    return info;
                };
                w.SelfUpdater.CheckNow();
            }, delegate { return !w.SelfUpdater.Checking; }, 5000, delegate
            {
                if (!w.SelfUpdater.Available) return "the newer release is not marked available";
                if (w.SelfUpdater.Status.IndexOf("StrataHome 99.0.0 is available (you have " + LauncherUpdater.CurrentVersion + ")") < 0) return "the versions are not named: " + w.SelfUpdater.Status;
                w.Server.RefreshState();
                return (string)w.Server.SelfUpdateButtonForTest.Content == "Update StrataHome" ? null : "button label: " + w.Server.SelfUpdateButtonForTest.Content;
            });
            Add("server: a staged update says it goes in on Exit", delegate
            {
                w.SelfUpdater.Staged = true;
                w.Server.RefreshState();
                return w.Server.SelfUpdateButtonForTest.IsEnabled && (string)w.Server.SelfUpdateButtonForTest.Content == "Installed on Exit" ? null
                    : "button is disabled or has an unclear label: " + w.Server.SelfUpdateButtonForTest.Content;
            });
            Add("server: StrataHome restore the checker, toggle and staged state", delegate
            {
                w.SelfUpdater.Staged = false;
                w.SelfUpdater.Available = false;
                w.SelfUpdater.Latest = "";
                w.SelfUpdater.CheckRelease = originalSelfCheck;
                w.Settings.AutoUpdateLauncher = selfWas;
                w.SaveSettings();
                w.Server.RefreshState();
            }, null, 0, delegate
            {
                return w.SelfUpdater.CheckRelease == originalSelfCheck && !w.SelfUpdater.Staged && !w.SelfUpdater.Available && w.Settings.AutoUpdateLauncher == selfWas
                    ? null : "the StrataHome card was not restored";
            });
            Add("server: the Strata engine card lists the builds with their averages", delegate
            {
                engineWas = w.Settings.EngineChoice;
                w.Server.RefreshEngine();
                return w.Server.EngineControlsPresent && w.Server.EngineRowCountForTest >= 1 ? null : "engine rows: " + w.Server.EngineRowCountForTest;
            });
            Add("server: the installed build is the checked one when nothing else is chosen", delegate
            {
                w.Settings.EngineChoice = "current";
                w.Server.RefreshEngine();
                return w.Server.EngineCheckedForTest == "current" ? null : "checked: " + w.Server.EngineCheckedForTest;
            });
            Add("server: choosing an older build saves the choice and names the copy it runs from", delegate
            {
                string older = w.Server.EngineOtherKeyForTest;
                if (older.Length == 0) { log.AppendLine("SKIP  only one engine build in this install"); return null; }
                w.Server.CheckEngineForTest(older);
                return w.Settings.EngineChoice == older && w.Server.EngineCheckedForTest == older && w.Server.EngineStatusForTest.IndexOf("engine-configs") >= 0 ? null
                    : "saved " + w.Settings.EngineChoice + ", checked " + w.Server.EngineCheckedForTest + ", status: " + w.Server.EngineStatusForTest;
            });
            Add("server: restore the engine choice", delegate
            {
                w.Settings.EngineChoice = engineWas;
                w.SaveSettings();
                w.Server.RefreshEngine();
            }, null, 0, delegate { return w.Settings.EngineChoice == engineWas ? null : "the engine choice was not restored"; });
            if (w.Opt.UiSmoke) return;
            bool live = w.Launcher.State == RunState.Ready || w.Launcher.State == RunState.External || w.Launcher.State == RunState.Unloaded;
            if (!live) { log.AppendLine("SKIP  the chat and server tests need a running Strata (state: " + w.Launcher.State + ")"); return; }
            Add("about page fills in from the running server", delegate { w.ShowTab("about"); }, delegate { return w.About.ConfigFieldCount > 0; }, 8000, delegate { return null; });
            Add("chat: a message streams in and is answered", delegate { w.ShowTab("chat"); w.Chat.ClearForTest(); w.Chat.TestSend("Reply with exactly the single word: pong"); },
                delegate { return w.Chat.MessageCount == 2 && !w.Chat.Busy; }, 60000, delegate
                {
                    ChatMsg m = w.Chat.LastMessage;
                    if (m == null || m.Role != "assistant") return "no assistant message";
                    if (m.Error.Length > 0) return "error: " + m.Error;
                    if (m.Text.ToLowerInvariant().IndexOf("pong") < 0) return "answer was '" + m.Text + "'";
                    if (m.Meta.IndexOf("tokens") < 0 || m.Meta.IndexOf("tok/s") < 0 || m.Meta.IndexOf("tokens") > m.Meta.IndexOf("tok/s")) return "tokens and tok/s are not shown together: '" + m.Meta + "'";
                    return null;
                });
            Add("chat: Stop ends a long answer", delegate { w.Chat.TestSend("Count from 1 to 400, one number per line, nothing else."); },
                delegate { return w.Chat.Busy; }, 20000, delegate { return null; });
            Add("chat: (waiting a moment for text to stream)", null, delegate { return Waited(1500); }, 3000, delegate { return null; });
            Add("chat: tokens and tok/s show live under the answer while it streams", null,
                delegate { string t = w.Chat.MetaTextOfLast; return t.IndexOf("tokens") >= 0 && t.IndexOf("tok/s") > t.IndexOf("tokens"); }, 8000, delegate { return null; });
            Add("chat: pressing Stop", delegate { w.Chat.StopForTest(); }, delegate { return !w.Chat.Busy; }, 15000, delegate
            {
                ChatMsg m = w.Chat.LastMessage;
                if (m == null || !m.Stopped) return "the message was not marked stopped";
                if (m.Meta.ToLowerInvariant().IndexOf("stopped") < 0) return "the meta line does not say stopped: '" + m.Meta + "'";
                if (m.Text.Length == 0 && m.Reasoning.Length == 0) return "nothing had streamed before the stop";
                return null;
            });
            Add("chat: a text file attached to a message reaches the model", delegate
            {
                tempFile = Path.Combine(Path.GetTempPath(), "stratahome-uitest.txt");
                File.WriteAllText(tempFile, "The secret word is pelican.");
                w.Chat.AttachForTest(tempFile);
            }, null, 0, delegate { return w.Chat.ChipCount == 1 ? null : "attachment chip count " + w.Chat.ChipCount; });
            Add("chat: ... and the model reads it", delegate { w.Chat.TestSend("What is the secret word in the attached file? Answer with just the word."); },
                delegate { return !w.Chat.Busy && w.Chat.LastMessage != null && w.Chat.LastMessage.Role == "assistant" && (w.Chat.LastMessage.Text.Length > 0 || w.Chat.LastMessage.Error.Length > 0) && w.Chat.ChipCount == 0; },
                60000, delegate
                {
                    ChatMsg m = w.Chat.LastMessage;
                    if (m.Error.Length > 0) return "error: " + m.Error;
                    return m.Text.ToLowerInvariant().IndexOf("pelican") >= 0 ? null : "answer was '" + m.Text + "'";
                });
            int count = 0;
            Add("chat: New chat clears it", delegate { count = w.Chat.MessageCount; w.Chat.NewChatForTest(); }, null, 0,
                delegate { return w.Chat.MessageCount == 0 && count > 0 ? null : "messages after clearing: " + w.Chat.MessageCount; });
            Add("chat: Undo brings it back", delegate { w.Chat.UndoForTest(); }, null, 0, delegate { return w.Chat.MessageCount == count ? null : "restored " + w.Chat.MessageCount + " of " + count; });
            Add("chat: cleaning up the test conversation", delegate { w.Chat.ClearForTest(); try { File.Delete(tempFile); } catch { } }, null, 0, delegate { return w.Chat.MessageCount == 0 ? null : "not cleared"; });
            Add("pill reflects the live state", delegate { w.UpdatePill(); }, null, 0, delegate { return null; });
            if (w.Opt.RestartTest) AddRestartSteps();
        }

        /// <summary>--uitest-restart: really saves another context from the About page, restarts Strata and asks /health, then puts the
        /// run config back exactly as it was. It restarts the server twice, so it is not part of the plain --uitest.</summary>
        void AddRestartSteps()
        {
            string path = null, original = null;
            int ctx0 = 0, target = 0;
            Add("context: (restart test) remember the real run config", delegate
            {
                ModelEntry m = w.SelectedModel;
                if (m == null || m.MaxContext < 16384) return;
                path = m.ConfigPath; original = File.ReadAllText(path); ctx0 = m.MaxContext;
                target = ctx0 == 131072 ? 65536 : 131072;
                w.ShowTab("about");
            }, null, 0, delegate { return path == null ? "no model with a context of 16K or more is selected" : null; });
            Add("context: Save and restart loads the model with the new size", delegate { if (path != null) w.About.ChooseContextForTest(target, true); },
                delegate { return path == null || (w.Launcher.State == RunState.Ready && w.Launcher.MaxContext == target); }, 240000,
                delegate { return path == null || (w.Launcher.MaxContext == target && RunConfig.ArgValue(path, "--max-context") == target.ToString()) ? null : "the server serves " + w.Launcher.MaxContext + ", the config says " + RunConfig.ArgValue(path, "--max-context"); });
            Add("context: and back to the original size", delegate { if (path != null) w.About.ChooseContextForTest(ctx0, true); },
                delegate { return path == null || (w.Launcher.State == RunState.Ready && w.Launcher.MaxContext == ctx0); }, 240000,
                delegate { return path == null || w.Launcher.MaxContext == ctx0 ? null : "the server serves " + w.Launcher.MaxContext + ", expected " + ctx0; });
            Add("context: the run config is put back exactly as it was", delegate
            {
                if (path != null && File.ReadAllText(path) != original) File.WriteAllText(path, original, new UTF8Encoding(false));   // same options, maybe another order: put the original text back
            }, null, 0, delegate
            {
                if (path == null) return null;
                return File.ReadAllText(path) == original && RunConfig.ArgValue(path, "--max-context") == ctx0.ToString() ? null : "the config is not back to the original";
            });
        }


        string md = "";
        string perfPath = "";
        int perfRealLines;
        string engineWas = "";

        static int CountLines(string path)
        {
            try { return File.ReadLines(path).Count(); } catch { return 0; }
        }
        int toasts;
        DateTime waitFrom;

        /// <summary>A /metrics sample with the given engine version and request rows, for the perf card tests.</summary>
        static Dictionary<string, object> Sample(string version, string requestsJson)
        {
            return new JavaScriptSerializer().DeserializeObject("{\"engine\":{\"version\":\"" + version + "\",\"model\":\"test\"},\"live\":{\"state\":\"idle\",\"queued\":0},\"requests\":[" + requestsJson + "]}") as Dictionary<string, object>;
        }

        bool Waited(int ms)
        {
            if (waitFrom == DateTime.MinValue) waitFrom = DateTime.UtcNow;
            if ((DateTime.UtcNow - waitFrom).TotalMilliseconds >= ms) { waitFrom = DateTime.MinValue; return true; }
            return false;
        }

        public void Start()
        {
            Build();
            timer.Interval = TimeSpan.FromMilliseconds(120);
            timer.Tick += delegate { Tick(); };
            timer.Start();
        }

        void Tick()
        {
            Step cur = index >= 0 && index < steps.Count ? steps[index] : null;
            if (cur != null && started)
            {
                bool ready = cur.Until == null ? true : cur.Until();
                bool timedOut = cur.Until != null && (DateTime.UtcNow - stepStart).TotalMilliseconds > cur.TimeoutMs;
                if (!ready && !timedOut) return;
                string problem = null;
                try { problem = (!ready && timedOut) ? "timed out after " + cur.TimeoutMs + " ms" : (cur.Verify != null ? cur.Verify() : null); }
                catch (Exception ex) { problem = "exception: " + ex.Message; }
                log.AppendLine((problem == null ? "PASS  " : "FAIL  ") + cur.Name + (problem == null ? "" : "  <- " + problem));
                if (problem != null) fails++;
                started = false;
            }
            index++;
            if (index >= steps.Count) { Finish(); return; }
            Step next = steps[index];
            stepStart = DateTime.UtcNow;
            try { if (next.Do != null) next.Do(); }
            catch (Exception ex) { log.AppendLine("FAIL  " + next.Name + "  <- exception in action: " + ex.Message); fails++; index++; if (index >= steps.Count) { Finish(); } return; }
            started = true;
        }

        void Finish()
        {
            timer.Stop();
            log.AppendLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
            try { Directory.CreateDirectory(Paths.Logs); File.WriteAllText(Path.Combine(Paths.Logs, "uitest.txt"), log.ToString()); } catch { }
            Application.Current.Shutdown(fails == 0 ? 0 : 1);
        }
    }
}
