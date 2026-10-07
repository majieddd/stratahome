using System;
using System.Collections.Generic;
using System.IO;
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

        public UiTest(MainWindow window) { w = window; }

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
            Add("server page lists the installed models", delegate { w.Server.RefreshAll(); }, null, 0,
                delegate { return w.Server.ModelRowCount == Math.Max(1, w.Models.Count) ? null : "model rows: " + w.Server.ModelRowCount; });
            Add("toast appears and goes away", delegate { toasts = w.ToastCount; w.Toast("info", "uitest", "hello", 700); }, null, 0, delegate { return w.ToastCount == toasts + 1 ? null : "toast not shown"; });
            Add("toast is removed after its time", null, delegate { return w.ToastCount == toasts; }, 3000, delegate { return null; });

            // ---- the live server
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
                    if (m.Meta.IndexOf("tokens") < 0) return "no token meta: '" + m.Meta + "'";
                    return null;
                });
            Add("chat: Stop ends a long answer", delegate { w.Chat.TestSend("Count from 1 to 400, one number per line, nothing else."); },
                delegate { return w.Chat.Busy; }, 20000, delegate { return null; });
            Add("chat: (waiting a moment for text to stream)", null, delegate { return Waited(1500); }, 3000, delegate { return null; });
            Add("chat: pressing Stop", delegate { w.Chat.StopForTest(); }, delegate { return !w.Chat.Busy; }, 15000, delegate
            {
                ChatMsg m = w.Chat.LastMessage;
                if (m == null || !m.Stopped) return "the message was not marked stopped";
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
        }

        string md = "";
        int toasts;
        DateTime waitFrom;

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
