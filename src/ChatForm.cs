using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace StrataHome
{
    /// <summary>A small native chat window that talks to the local server, so using Strata does not need a browser.</summary>
    internal sealed class ChatForm : Form
    {
        readonly Launcher launcher;
        readonly Settings settings;
        readonly List<object> history = new List<object>();
        readonly RichTextBox view = new RichTextBox();
        readonly TextBox input = new TextBox();
        readonly Button sendButton = new Button();
        readonly Button newButton = new Button();
        readonly ComboBox effort = new ComboBox();
        readonly Label status = new Label();
        readonly Font fontBold = new Font("Segoe UI", 10f, FontStyle.Bold);
        readonly Font fontItalic = new Font("Segoe UI", 10f, FontStyle.Italic);
        HttpWebRequest current;
        bool busy;
        bool abortedByUser;

        public ChatForm(Launcher launcher, Settings settings)
        {
            this.launcher = launcher;
            this.settings = settings;

            Text = "StrataHome chat";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(720, 640);
            MinimumSize = new Size(480, 420);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Panel top = new Panel(); top.Dock = DockStyle.Top; top.Height = 40; top.Padding = new Padding(8, 8, 8, 4);
            Label l = new Label(); l.Text = "Thinking"; l.AutoSize = true; l.Location = new Point(10, 12);
            effort.DropDownStyle = ComboBoxStyle.DropDownList;
            effort.Items.AddRange(new object[] { "none", "low", "medium", "high" });
            effort.SelectedItem = Array.IndexOf(new string[] { "none", "low", "medium", "high" }, settings.Effort) >= 0 ? settings.Effort : "medium";
            effort.Location = new Point(70, 8); effort.Width = 90;
            newButton.Text = "New chat"; newButton.Location = new Point(172, 7); newButton.Size = new Size(84, 26);
            newButton.Click += delegate { NewChat(); };
            status.AutoSize = false; status.Location = new Point(270, 12); status.Size = new Size(430, 20);
            status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; status.ForeColor = Color.DimGray;
            top.Controls.AddRange(new Control[] { l, effort, newButton, status });

            Panel bottom = new Panel(); bottom.Dock = DockStyle.Bottom; bottom.Height = 92; bottom.Padding = new Padding(8);
            input.Multiline = true; input.ScrollBars = ScrollBars.Vertical; input.Font = new Font("Segoe UI", 10f);
            input.Dock = DockStyle.Fill;
            input.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Send(); }
            };
            sendButton.Text = "Send"; sendButton.Dock = DockStyle.Right; sendButton.Width = 86;
            sendButton.Click += delegate { Send(); };
            Panel gap = new Panel(); gap.Dock = DockStyle.Right; gap.Width = 8;
            bottom.Controls.Add(input); bottom.Controls.Add(gap); bottom.Controls.Add(sendButton);

            view.Dock = DockStyle.Fill; view.ReadOnly = true; view.BackColor = Color.White;
            view.Font = new Font("Segoe UI", 10f); view.BorderStyle = BorderStyle.None; view.Margin = new Padding(8);
            view.DetectUrls = false;

            Controls.Add(view); Controls.Add(bottom); Controls.Add(top);
            Dpi.Apply(this);
            FormClosing += delegate { CancelRequest(); settings.Effort = Convert.ToString(effort.SelectedItem); settings.Save(); };
            Shown += delegate { input.Focus(); ShowHint(); };
        }

        string demoPng;

        /// <summary>--screenshot-chat: send one message, wait for the answer, save the window as a PNG.</summary>
        public void RunDemo(string prompt, string png)
        {
            demoPng = png;
            effort.SelectedItem = "low";
            input.Text = prompt;
            Send();
        }

        void ShowHint()
        {
            RunState s = launcher.State;
            if (!(s == RunState.Ready || s == RunState.Unloaded || s == RunState.External))
                status.Text = "Strata is not ready yet (" + s + "). Messages need a running server.";
            else if (s == RunState.Unloaded)
                status.Text = "The model is unloaded; the first message loads it (about a minute).";
            else
                status.Text = "Connected to " + launcher.ApiUrl;
        }

        void NewChat()
        {
            CancelRequest();
            history.Clear();
            view.Clear();
            ShowHint();
            input.Focus();
        }

        void Send()
        {
            if (busy) { CancelRequest(); return; }
            string text = input.Text.Trim();
            if (text.Length == 0) return;
            RunState s = launcher.State;
            if (!(s == RunState.Ready || s == RunState.Unloaded || s == RunState.External))
            {
                status.Text = "Strata is not ready (" + s + "). Start it from the main window.";
                return;
            }
            input.Clear();
            Dictionary<string, object> msg = new Dictionary<string, object>();
            msg["role"] = "user"; msg["content"] = text;
            history.Add(msg);
            Append("You\n", Color.FromArgb(60, 60, 60), FontStyle.Bold);
            Append(text + "\n\n", Color.Black, FontStyle.Regular);
            busy = true; abortedByUser = false;
            sendButton.Text = "Stop";
            status.Text = "Waiting for the model...";
            string eff = Convert.ToString(effort.SelectedItem);
            Thread t = new Thread(delegate() { Run(eff); });
            t.IsBackground = true;
            t.Start();
        }

        void CancelRequest()
        {
            abortedByUser = true;
            try { if (current != null) current.Abort(); } catch { }
        }

        void Run(string eff)
        {
            StringBuilder answer = new StringBuilder();
            bool sawReasoning = false, sawAnswer = false;
            int chunks = 0, tokens = 0;
            DateTime t0 = DateTime.UtcNow;
            DateTime firstToken = DateTime.MinValue;
            string error = null;
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["model"] = "strata";
                body["messages"] = history.ToArray();
                body["stream"] = true;
                Dictionary<string, object> so = new Dictionary<string, object>();
                so["include_usage"] = true;
                body["stream_options"] = so;
                body["max_tokens"] = 16384;
                body["temperature"] = 1.0;
                body["top_p"] = 0.95;
                body["top_k"] = 20;
                body["reasoning_effort"] = eff;
                byte[] payload = Encoding.UTF8.GetBytes(js.Serialize(body));

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(launcher.ApiUrl + "/chat/completions");
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Proxy = null;
                req.Timeout = 15 * 60 * 1000;
                req.ReadWriteTimeout = 15 * 60 * 1000;
                req.ContentLength = payload.Length;
                current = req;
                using (Stream rs = req.GetRequestStream()) rs.Write(payload, 0, payload.Length);

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (!line.StartsWith("data:")) continue;
                        string data = line.Substring(5).Trim();
                        if (data == "[DONE]") break;
                        Dictionary<string, object> o = js.DeserializeObject(data) as Dictionary<string, object>;
                        if (o == null) continue;
                        object u;
                        if (o.TryGetValue("usage", out u) && u is Dictionary<string, object>)
                        {
                            object ct;
                            if (((Dictionary<string, object>)u).TryGetValue("completion_tokens", out ct) && ct != null) tokens = Convert.ToInt32(ct);
                        }
                        IList choices = o.ContainsKey("choices") ? o["choices"] as IList : null;
                        if (choices == null || choices.Count == 0) continue;
                        Dictionary<string, object> c0 = choices[0] as Dictionary<string, object>;
                        Dictionary<string, object> delta = c0 != null && c0.ContainsKey("delta") ? c0["delta"] as Dictionary<string, object> : null;
                        if (delta == null) continue;
                        string reasoning = delta.ContainsKey("reasoning_content") ? delta["reasoning_content"] as string : null;
                        string content = delta.ContainsKey("content") ? delta["content"] as string : null;
                        if (!string.IsNullOrEmpty(reasoning))
                        {
                            chunks++;
                            if (firstToken == DateTime.MinValue) firstToken = DateTime.UtcNow;
                            if (!sawReasoning) { sawReasoning = true; Ui("Strata\n", Color.FromArgb(60, 60, 60), FontStyle.Bold); }
                            Ui(reasoning, Color.Gray, FontStyle.Italic);
                        }
                        if (!string.IsNullOrEmpty(content))
                        {
                            chunks++;
                            if (firstToken == DateTime.MinValue) firstToken = DateTime.UtcNow;
                            if (!sawAnswer)
                            {
                                sawAnswer = true;
                                if (sawReasoning) Ui("\n\n", Color.Black, FontStyle.Regular);
                                else Ui("Strata\n", Color.FromArgb(60, 60, 60), FontStyle.Bold);
                            }
                            answer.Append(content);
                            Ui(content, Color.Black, FontStyle.Regular);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!abortedByUser) error = ex.Message;
            }
            finally { current = null; }

            if (answer.Length > 0)
            {
                Dictionary<string, object> msg = new Dictionary<string, object>();
                msg["role"] = "assistant"; msg["content"] = answer.ToString();
                history.Add(msg);
            }
            else if (history.Count > 0)
            {
                history.RemoveAt(history.Count - 1);       // nothing came back: drop the unanswered question
            }
            double secs = firstToken == DateTime.MinValue ? 0 : (DateTime.UtcNow - firstToken).TotalSeconds;
            int shown = tokens > 0 ? tokens : chunks;
            string summary = abortedByUser ? "Stopped." : (shown > 0 && secs > 0.2
                ? shown + " tokens, " + (shown / secs).ToString("0") + " tokens/s"
                : "Done.");
            if (error != null) Ui("\n[error] " + error, Color.Firebrick, FontStyle.Regular);
            Ui("\n\n", Color.Black, FontStyle.Regular);
            Done(error != null ? "Error: " + error : summary);
        }

        void Ui(string text, Color color, FontStyle style)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action<string, Color, FontStyle>(Append), text, color, style); } catch { }
        }

        void Done(string text)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    busy = false;
                    sendButton.Text = "Send";
                    status.Text = text;
                    input.Focus();
                    if (demoPng != null)
                    {
                        string path = demoPng;
                        demoPng = null;
                        try { WindowShot.Save(this, path); } catch { }
                    }
                }));
            }
            catch { }
        }

        void Append(string text, Color color, FontStyle style)
        {
            view.SelectionStart = view.TextLength;
            view.SelectionLength = 0;
            view.SelectionColor = color;
            view.SelectionFont = style == FontStyle.Bold ? fontBold : style == FontStyle.Italic ? fontItalic : view.Font;
            view.AppendText(text);
            view.SelectionStart = view.TextLength;
            view.ScrollToCaret();
        }
    }
}

