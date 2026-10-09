using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

using System.Windows.Threading;

namespace StrataHome
{
    internal sealed class Attachment
    {
        public string Name = "";
        public string Text = "";
    }

    internal sealed class ChatMsg
    {
        public string Role = "user";
        public string Text = "";
        public string Reasoning = "";
        public string Meta = "";
        public string Error = "";
        public bool Stopped;
        public double? ThinkSecs;
        public DateTime Time = DateTime.Now;
        public List<Attachment> Files = new List<Attachment>();
    }

    /// <summary>One streaming /v1/chat/completions call, run on a worker thread; the callbacks fire on that thread.</summary>
    internal sealed class ChatRequest
    {
        public string Url, ApiKey;
        public Dictionary<string, object> Body;
        public Action<string> OnReasoning, OnContent;
        public Action<Dictionary<string, object>> OnUsage, OnTimings;
        public volatile bool Aborted;
        HttpWebRequest current;

        public void Abort()
        {
            Aborted = true;
            try { if (current != null) current.Abort(); } catch { }
        }

        /// <summary>Returns an error message, or null when it ended normally (or was stopped).</summary>
        public string Run()
        {
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                byte[] payload = Encoding.UTF8.GetBytes(js.Serialize(Body));
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Proxy = null;
                req.Timeout = 15 * 60 * 1000;
                req.ReadWriteTimeout = 15 * 60 * 1000;
                req.ContentLength = payload.Length;
                if (!string.IsNullOrEmpty(ApiKey)) req.Headers["Authorization"] = "Bearer " + ApiKey;
                current = req;
                using (Stream rs = req.GetRequestStream()) rs.Write(payload, 0, payload.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (!line.StartsWith("data:")) continue;                // ": keep-alive" comments while a long prompt is read
                        string data = line.Substring(5).Trim();
                        if (data == "[DONE]") continue;
                        Dictionary<string, object> o;
                        try { o = js.DeserializeObject(data) as Dictionary<string, object>; } catch { continue; }
                        if (o == null) continue;
                        object err = J.Get(o, "error");
                        if (err != null) return J.Str(err, "message") ?? "the engine reported an error";
                        Dictionary<string, object> usage = J.Get(o, "usage") as Dictionary<string, object>;
                        if (usage != null && OnUsage != null) OnUsage(usage);
                        Dictionary<string, object> timings = J.Get(o, "timings") as Dictionary<string, object>;
                        if (timings != null && OnTimings != null) OnTimings(timings);
                        IList choices = J.List(o, "choices");
                        if (choices.Count == 0) continue;
                        object delta = J.Get(choices[0], "delta");
                        string reasoning = J.Str(delta, "reasoning_content"), content = J.Str(delta, "content");
                        if (!string.IsNullOrEmpty(reasoning) && OnReasoning != null) OnReasoning(reasoning);
                        if (!string.IsNullOrEmpty(content) && OnContent != null) OnContent(content);
                    }
                }
                return null;
            }
            catch (WebException ex)
            {
                if (Aborted) return null;
                string msg = ex.Message;
                HttpWebResponse r = ex.Response as HttpWebResponse;
                if (r != null)
                {
                    msg = "HTTP " + (int)r.StatusCode;
                    try
                    {
                        using (StreamReader sr = new StreamReader(r.GetResponseStream()))
                        {
                            string m = J.Str(new JavaScriptSerializer().DeserializeObject(sr.ReadToEnd()), "error", "message");
                            if (!string.IsNullOrEmpty(m)) msg = m;
                        }
                    }
                    catch { }
                    if ((int)r.StatusCode == 401) msg = "This server needs an API key: add it under About > Settings.";
                }
                return msg;
            }
            catch (Exception ex)
            {
                return Aborted ? null : ex.Message;
            }
        }
    }

    /// <summary>The Chat tab: the web app's chat (messages, thinking block, attachments, composer) as native WPF.</summary>
    internal sealed class ChatView
    {
        public readonly FrameworkElement Root;
        readonly MainWindow w;

        sealed class MsgView
        {
            public FrameworkElement Root;
            public Border Bubble, ThinkBox;
            public TextBlock ThinkTitle, ThinkBody, Meta;
            public RichTextBox Rtb;
            public Button Copy;
            public Border ThinkBodyBox;
            public IconView Chevron;
            public bool Touched, Open;
        }

        readonly List<ChatMsg> messages = new List<ChatMsg>();
        readonly Dictionary<ChatMsg, MsgView> views = new Dictionary<ChatMsg, MsgView>();
        readonly List<Attachment> attachments = new List<Attachment>();
        readonly object gate = new object();
        ScrollViewer scroll;
        StackPanel list;
        FrameworkElement empty;
        TextBlock emptySub, hint, placeholder;
        TextBox input;
        Border composer;
        WrapPanel chips;
        Button sendBtn, stopBtn;
        ChatRequest running;
        ChatMsg active;
        bool busy, dirty;
        DispatcherTimer painter;
        string overrideEffort;
        Action demoDone;
        List<ChatMsg> undoBackup;

        static readonly Regex TextExt = new Regex(@"\.(txt|md|markdown|rst|tex|py|pyi|ipynb|js|mjs|cjs|ts|tsx|jsx|vue|svelte|json|jsonl|csv|tsv|log|ya?ml|toml|ini|cfg|conf|env|xml|html?|css|scss|less|c|cc|cpp|cxx|h|hh|hpp|cu|cuh|rs|go|java|kt|kts|swift|rb|php|pl|lua|r|jl|scala|sql|sh|bash|zsh|fish|ps1|psm1|bat|cmd|diff|patch|gradle|cmake|mk|dockerfile|gitignore|proto|graphql|cs)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public ChatView(MainWindow window)
        {
            w = window;
            Grid root = new Grid();
            root.RowDefinitions.Add(Ui.Row(Ui.Star(1)));
            root.RowDefinitions.Add(Ui.Row(Ui.Auto));
            root.AllowDrop = true;
            root.Background = Brushes.Transparent;
            root.DragOver += delegate(object s, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            root.Drop += delegate(object s, DragEventArgs e)
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null) AddFiles(files);
                input.Focus();
            };

            scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            StackPanel col = new StackPanel();
            col.MaxWidth = 860;
            col.Margin = new Thickness(24, 32, 24, 24);
            list = new StackPanel();
            // the empty state: "Ask anything"
            StackPanel e0 = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 90, 0, 0) };
            TextBlock t = Ui.Text("Ask anything", 40, "StInk", "black");
            t.HorizontalAlignment = HorizontalAlignment.Center;
            e0.Children.Add(t);
            emptySub = Ui.Text("The model runs on this PC. Nothing leaves it.", 16, "StInkMuted");
            emptySub.HorizontalAlignment = HorizontalAlignment.Center;
            emptySub.Margin = new Thickness(0, 12, 0, 0);
            e0.Children.Add(emptySub);
            empty = e0;
            col.Children.Add(empty);
            col.Children.Add(list);
            scroll.Content = col;
            root.Children.Add(Ui.At(scroll, 0, 0));
            root.Children.Add(Ui.At(BuildComposer(), 1, 0));
            Root = root;

            painter = new DispatcherTimer();
            painter.Interval = TimeSpan.FromMilliseconds(60);
            painter.Tick += delegate { if (dirty && active != null) { dirty = false; Paint(active, true); ScrollDown(false); } };
            LoadChat();
            RenderAll();
            root.SizeChanged += delegate { input.MaxHeight = Math.Max(96, root.ActualHeight * 0.4); };
        }

        // ------------------------------------------------------------------ composer

        FrameworkElement BuildComposer()
        {
            composer = new Border();
            composer.MaxWidth = 860;
            composer.Margin = new Thickness(24, 0, 36, 24);   // 12 more on the right: the message column has a scroll bar beside it
            composer.Padding = new Thickness(10);
            composer.BorderThickness = new Thickness(1);
            composer.CornerRadius = new CornerRadius(20);
            composer.SetResourceReference(Border.BackgroundProperty, "StSurface");
            composer.SetResourceReference(Border.BorderBrushProperty, "StLine");
            composer.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, Opacity = 0.09, BlurRadius = 24, ShadowDepth = 6, Direction = 270 };
            StackPanel sp = new StackPanel();
            chips = new WrapPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(2, 2, 0, 4) };
            sp.Children.Add(chips);

            Grid box = new Grid();
            input = new TextBox();
            input.Style = Ui.Style("StComposerBox");
            input.AcceptsReturn = true;
            input.TextWrapping = TextWrapping.Wrap;
            input.MinHeight = 48;
            input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            input.PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; Send(); }
            };
            input.TextChanged += delegate { placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            input.GotKeyboardFocus += delegate { composer.SetResourceReference(Border.BorderBrushProperty, "StFocus"); };
            input.LostKeyboardFocus += delegate { composer.SetResourceReference(Border.BorderBrushProperty, "StLine"); };
            placeholder = Ui.Text("Ask anything\u2026", 16, "StInkMuted");
            placeholder.Margin = new Thickness(8, 6, 0, 0);
            placeholder.IsHitTestVisible = false;
            placeholder.VerticalAlignment = VerticalAlignment.Top;
            box.Children.Add(input);
            box.Children.Add(placeholder);
            sp.Children.Add(box);

            Grid bar = new Grid();
            bar.Margin = new Thickness(0, 8, 0, 0);
            // columns: attach, new chat, save, sampling | spacer | hint | stop | send
            for (int i = 0; i < 8; i++) bar.ColumnDefinitions.Add(Ui.Col(i == 4 ? Ui.Star(1) : Ui.Auto));
            Button attach = Ui.IconButton("attach", "Attach a text file (or drop it here)", delegate { PickFiles(); });
            Button newChat = Ui.IconButton("new-chat", "New chat", delegate { NewChat(); });
            Button save = Ui.IconButton("download", "Save this chat as Markdown", delegate { SaveChat(); });
            Button sampling = Ui.IconButton("settings", "Sampling and thinking", delegate { w.Drawer.Open(); });
            bar.Children.Add(Ui.At(attach, 0, 0)); bar.Children.Add(Ui.At(newChat, 0, 1));
            bar.Children.Add(Ui.At(save, 0, 2)); bar.Children.Add(Ui.At(sampling, 0, 3));
            hint = Ui.Text("Shift+Enter: new line", 12, "StInkMuted");
            hint.VerticalAlignment = VerticalAlignment.Center;
            hint.Margin = new Thickness(0, 0, 8, 0);
            bar.Children.Add(Ui.At(hint, 0, 5));
            stopBtn = Ui.Btn("secondary", "Stop", "stop", delegate { if (running != null) running.Abort(); });
            stopBtn.Visibility = Visibility.Collapsed;
            stopBtn.Margin = new Thickness(0, 0, 6, 0);
            bar.Children.Add(Ui.At(stopBtn, 0, 6));
            sendBtn = Ui.Btn("primary", "", "send", delegate { Send(); });
            sendBtn.Width = 42;
            sendBtn.Padding = new Thickness(0);
            bar.Children.Add(Ui.At(sendBtn, 0, 7));
            sp.Children.Add(bar);
            composer.Child = sp;
            return composer;
        }

        public void FocusInput() { input.Focus(); }

        public void Redact() { }

        public void RefreshState()
        {
            string model = w.Launcher.ServedModel.Length > 0 ? w.Launcher.ServedModel : (w.SelectedModel != null ? w.SelectedModel.ModelName : "The model");
            RunState s = w.Launcher.State;
            bool ok = s == RunState.Ready || s == RunState.Unloaded || s == RunState.External;
            emptySub.Text = ok ? model + " runs on this PC. Nothing leaves it."
                : s == RunState.Starting || s == RunState.Loading ? "Strata is starting. You can type while it loads."
                : "Strata is not running. Start it on the Server tab.";
        }

        // ------------------------------------------------------------------ messages

        void RenderAll()
        {
            list.Children.Clear();
            views.Clear();
            empty.Visibility = messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < messages.Count; i++) AddView(messages[i], i == 0);
            ScrollDown(true);
        }

        MsgView AddView(ChatMsg m, bool first)
        {
            MsgView v = m.Role == "user" ? BuildUser(m) : BuildAssistant(m);
            v.Root.Margin = new Thickness(0, first ? 0 : 24, 0, 0);
            list.Children.Add(v.Root);
            views[m] = v;
            if (m.Role != "user") Paint(m, false);
            return v;
        }

        MsgView BuildUser(ChatMsg m)
        {
            MsgView v = new MsgView();
            StackPanel sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 720 };
            if (m.Files.Count > 0)
            {
                WrapPanel wp = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 8) };
                foreach (Attachment f in m.Files) wp.Children.Add(Chip(f.Name, null));
                sp.Children.Add(wp);
            }
            Border b = new Border { CornerRadius = new CornerRadius(20, 20, 6, 20), Padding = new Thickness(16, 12, 16, 12), HorizontalAlignment = HorizontalAlignment.Right };
            b.SetResourceReference(Border.BackgroundProperty, "StAccent");
            TextBox tb = new TextBox();
            tb.Text = m.Text;
            tb.IsReadOnly = true;
            tb.BorderThickness = new Thickness(0);
            tb.Background = Brushes.Transparent;
            tb.TextWrapping = TextWrapping.Wrap;
            tb.FontFamily = Fonts.Regular;
            tb.FontSize = 16;
            tb.Padding = new Thickness(0);
            tb.FocusVisualStyle = null;
            tb.SetResourceReference(Control.ForegroundProperty, "StAccentInk");
            tb.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));
            b.Child = tb;
            sp.Children.Add(b);
            TextBlock meta = Ui.Text("You \u00b7 " + m.Time.ToString("t"), 12, "StInkMuted");
            meta.HorizontalAlignment = HorizontalAlignment.Right;
            meta.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(meta);
            v.Root = sp;
            return v;
        }

        FrameworkElement Chip(string name, Action remove)
        {
            Border c = new Border { Height = 30, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 0, remove != null ? 4 : 10, 0) };
            c.SetResourceReference(Border.BackgroundProperty, "StSurface2");
            c.SetResourceReference(Border.BorderBrushProperty, "StLine");
            StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal };
            IconView iv = new IconView("attach", 16);
            iv.SetResourceReference(TextElement.ForegroundProperty, "StInkSoft");
            iv.Margin = new Thickness(0, 0, 6, 0);
            iv.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(iv);
            TextBlock t = Ui.Text(name, 12, "StInkSoft");
            t.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(t);
            if (remove != null)
            {
                Button x = Ui.IconButton("trash", "Remove", delegate { remove(); });
                x.Width = x.Height = 22;
                x.Margin = new Thickness(4, 0, 0, 0);
                sp.Children.Add(x);
            }
            c.Child = sp;
            return c;
        }

        MsgView BuildAssistant(ChatMsg m)
        {
            MsgView v = new MsgView();
            StackPanel sp = new StackPanel();

            // thinking: a collapsible block, hidden until there is some
            Border think = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
            think.SetResourceReference(Border.BackgroundProperty, "StSurface2");
            think.SetResourceReference(Border.BorderBrushProperty, "StLine");
            StackPanel tsp = new StackPanel();
            Button head = new Button();
            head.Style = Ui.Style("StBtnBase");
            head.Height = 40;
            head.Background = Brushes.Transparent;
            head.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            head.Template = FlatButtonTemplate();
            Grid hg = new Grid { Margin = new Thickness(14, 0, 14, 0) };
            hg.ColumnDefinitions.Add(Ui.Col(Ui.Auto)); hg.ColumnDefinitions.Add(Ui.Col(Ui.Star(1))); hg.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            IconView ti = new IconView("thinking", 16);
            ti.SetResourceReference(TextElement.ForegroundProperty, "StInkMuted");
            ti.VerticalAlignment = VerticalAlignment.Center;
            ti.Margin = new Thickness(0, 0, 8, 0);
            hg.Children.Add(Ui.At(ti, 0, 0));
            v.ThinkTitle = Ui.Text("Thoughts", 13, "StInkMuted", "medium");
            v.ThinkTitle.VerticalAlignment = VerticalAlignment.Center;
            hg.Children.Add(Ui.At(v.ThinkTitle, 0, 1));
            v.Chevron = new IconView("chevron", 16);
            v.Chevron.SetResourceReference(TextElement.ForegroundProperty, "StInkMuted");
            v.Chevron.VerticalAlignment = VerticalAlignment.Center;
            v.Chevron.RenderTransformOrigin = new Point(0.5, 0.5);
            hg.Children.Add(Ui.At(v.Chevron, 0, 2));
            head.Content = hg;
            tsp.Children.Add(head);
            v.ThinkBody = Ui.Text("", 13, "StInkMuted", "regular", true);
            v.ThinkBody.LineHeight = 20.8;
            ScrollViewer tsv = new ScrollViewer { MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(14, 0, 14, 14) };
            tsv.Content = v.ThinkBody;
            v.ThinkBodyBox = new Border { Child = tsv, Visibility = Visibility.Collapsed };
            tsp.Children.Add(v.ThinkBodyBox);
            think.Child = tsp;
            v.ThinkBox = think;
            head.Click += delegate
            {
                v.Touched = true;
                SetThinkOpen(v, m, !v.Open);
            };
            sp.Children.Add(think);

            Border bubble = new Border { CornerRadius = new CornerRadius(20, 20, 20, 6), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 16, 12) };
            bubble.SetResourceReference(Border.BackgroundProperty, "StSurface");
            bubble.SetResourceReference(Border.BorderBrushProperty, "StLine");
            v.Bubble = bubble;
            sp.Children.Add(bubble);

            StackPanel meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            v.Meta = Ui.Text("", 12, "StInkMuted");
            v.Meta.VerticalAlignment = VerticalAlignment.Center;
            meta.Children.Add(v.Meta);
            v.Copy = Ui.IconButton("copy", "Copy the answer", delegate { Ui.Copy(m.Text); w.Toast("success", "Copied", "", 1500); });
            v.Copy.Width = v.Copy.Height = 28;
            v.Copy.Margin = new Thickness(8, 0, 0, 0);
            v.Copy.Visibility = Visibility.Collapsed;
            meta.Children.Add(v.Copy);
            sp.Children.Add(meta);
            // full width up to 720 px, left aligned (the web app's .st-msg { width: 100%; max-width: 720px })
            Grid frame = new Grid();
            ColumnDefinition main = Ui.Col(Ui.Star(1));
            main.MaxWidth = 720;
            frame.ColumnDefinitions.Add(main);
            frame.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            frame.Children.Add(Ui.At(sp, 0, 0));
            v.Root = frame;
            return v;
        }

        static ControlTemplate FlatButtonTemplate()
        {
            ControlTemplate t = new ControlTemplate(typeof(Button));
            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            t.VisualTree = cp;
            return t;
        }

        void SetThinkOpen(MsgView v, ChatMsg m, bool open)
        {
            v.Open = open;
            v.ThinkBodyBox.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            v.Chevron.RenderTransform = new RotateTransform(open ? 180 : 0);
            if (open) v.ThinkBody.Text = m.Reasoning;
        }

        /// <summary>Redraws one assistant message from its data (the web app's updateAssistant).</summary>
        void Paint(ChatMsg m, bool streaming)
        {
            MsgView v;
            if (!views.TryGetValue(m, out v)) return;
            string reasoning, text;
            lock (m) { reasoning = m.Reasoning; text = m.Text; }
            if (reasoning.Length > 0)
            {
                v.ThinkBox.Visibility = Visibility.Visible;
                bool thinkingNow = streaming && text.Length == 0;
                v.ThinkTitle.Text = thinkingNow ? "Thinking\u2026" : m.ThinkSecs != null ? "Thought for " + Fmt.N(m.ThinkSecs, 1) + " s" : "Thoughts";
                if (v.Open || thinkingNow) v.ThinkBody.Text = reasoning;
                if (thinkingNow && w.Settings.ShowThinking && !v.Touched && !v.Open) SetThinkOpen(v, m, true);
                if (!thinkingNow && v.Open && !v.Touched) SetThinkOpen(v, m, false);
            }
            if (m.Error.Length > 0)
            {
                Border e = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10, 14, 10) };
                e.SetResourceReference(Border.BackgroundProperty, "StDangerTint");
                e.Child = Ui.Text(m.Error, 14, "StDangerText", "regular", true);
                v.Bubble.Child = e;
                v.Rtb = null;
            }
            else if (text.Length == 0 && streaming)
            {
                v.Bubble.Child = Ui.Text(reasoning.Length > 0 ? "Writing" : "\u2026", 16, "StInkMuted");
                v.Rtb = null;
            }
            else if (text.Length == 0)
            {
                v.Bubble.Child = Ui.Text(m.Stopped ? "Stopped before any answer." : "(no answer)", 16, "StInkMuted");
                v.Rtb = null;
            }
            else
            {
                if (v.Rtb == null) { v.Rtb = MakeRtb(); v.Bubble.Child = v.Rtb; }
                v.Rtb.Document = Markdown.Build(text);
            }
            v.Meta.Text = m.Meta.Length > 0 ? m.Meta : streaming ? LiveText() : (m.Stopped ? "Stopped" : "");
            v.Copy.Visibility = !streaming && text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        double liveTokens, liveRate;

        /// <summary>While an answer streams: the engine's running token count and speed (the Monitor's numbers), under the answer.</summary>
        string LiveText()
        {
            double? g = J.Dbl(w.Metrics.Last, "live", "generated"), r = J.Dbl(w.Metrics.Last, "live", "tok_s");
            if (g > 0) { liveTokens = g.Value; liveRate = r ?? 0; }
            return liveTokens > 0 ? Fmt.N(liveTokens) + " tokens" + (liveRate > 0 ? " \u00b7 " + Fmt.N(liveRate, 1) + " tok/s" : "") : "";
        }

        RichTextBox MakeRtb()
        {
            RichTextBox r = new RichTextBox();
            r.IsReadOnly = true;
            r.IsDocumentEnabled = true;
            r.BorderThickness = new Thickness(0);
            r.Background = Brushes.Transparent;
            r.Padding = new Thickness(0);
            r.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            r.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            r.FocusVisualStyle = null;
            r.SetResourceReference(Control.ForegroundProperty, "StInkSoft");
            r.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x70, 0x40, 0x5D, 0xE6));
            // the chat scrolls, not each answer: pass the wheel up
            r.PreviewMouseWheel += delegate(object s, MouseWheelEventArgs e)
            {
                e.Handled = true;
                MouseWheelEventArgs a = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta);
                a.RoutedEvent = UIElement.MouseWheelEvent;
                a.Source = s;
                scroll.RaiseEvent(a);
            };
            return r;
        }

        void ScrollDown(bool force)
        {
            if (force || scroll.ScrollableHeight - scroll.VerticalOffset < 120) scroll.ScrollToEnd();
        }

        // ------------------------------------------------------------------ send

        string ModelName()
        {
            string m = w.Launcher.ServedModel;
            return string.IsNullOrEmpty(m) ? "strata" : m;
        }

        List<object> ApiMessages()
        {
            List<object> o = new List<object>();
            foreach (ChatMsg m in messages)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                if (m.Role == "user")
                {
                    d["role"] = "user";
                    StringBuilder sb = new StringBuilder(m.Text);
                    foreach (Attachment f in m.Files)
                    {
                        if (f.Text == null) continue;
                        // a file's text, fenced with more backticks than it contains itself
                        int longest = 2;
                        foreach (Match x in Regex.Matches(f.Text, "`+")) longest = Math.Max(longest, x.Length);
                        string fence = new string('`', longest + 1);
                        if (sb.Length > 0) sb.Append("\n\n");
                        sb.Append("File: ").Append(f.Name).Append('\n').Append(fence).Append('\n').Append(f.Text).Append('\n').Append(fence);
                    }
                    d["content"] = sb.ToString();
                    o.Add(d);
                }
                else if (m.Error.Length == 0 && m.Text.Length > 0)
                {
                    d["role"] = "assistant";
                    d["content"] = m.Text;
                    o.Add(d);
                }
            }
            return o;
        }

        public void Send()
        {
            if (busy) return;
            if (w.Updater.Busy) { w.Toast("warn", "Strata is updating", "Your message is kept. Send it after the update finishes.", 4500); return; }
            string text = input.Text.Trim();
            if (text.Length == 0 && attachments.Count == 0) return;
            RunState s = w.Launcher.State;
            if (!(s == RunState.Ready || s == RunState.Unloaded || s == RunState.External))
            {
                w.Toast("warn", s == RunState.Starting || s == RunState.Loading ? "Strata is still loading" : "Strata is not running",
                        s == RunState.Starting || s == RunState.Loading ? "Your message is kept: send it once the pill says Idle." : "Start it on the Server tab first.", 4500);
                return;
            }
            ChatMsg user = new ChatMsg();
            user.Role = "user"; user.Text = text; user.Files = new List<Attachment>(attachments);
            messages.Add(user);
            attachments.Clear();
            RenderChips();
            input.Clear();
            ChatMsg m = new ChatMsg();
            m.Role = "assistant";
            List<object> api = ApiMessages();
            messages.Add(m);
            empty.Visibility = Visibility.Collapsed;
            AddView(user, list.Children.Count == 0);
            AddView(m, false);
            ScrollDown(true);
            active = m;

            Settings st = w.Settings;
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["model"] = ModelName();
            body["messages"] = api.ToArray();
            body["stream"] = true;
            Dictionary<string, object> so = new Dictionary<string, object>();
            so["include_usage"] = true;
            body["stream_options"] = so;
            body["reasoning_effort"] = overrideEffort ?? st.Effort;
            if (st.Temperature > 0) { body["temperature"] = st.Temperature; body["top_p"] = st.TopP; body["top_k"] = st.TopK; }
            else body["temperature"] = 0;
            int n;
            if (st.Seed.Length > 0 && int.TryParse(st.Seed, out n)) body["seed"] = n;
            if (st.MaxTokens.Length > 0 && int.TryParse(st.MaxTokens, out n)) body["max_tokens"] = n;
            string cvec = J.Str(w.Metrics.Last, "engine", "cvec");
            if (!string.IsNullOrEmpty(cvec) && cvec != "0") body["experimental_speed_projection"] = true;

            ChatRequest rq = new ChatRequest();
            rq.Url = w.Launcher.ApiUrl + "/chat/completions";
            rq.ApiKey = st.ApiKey;
            rq.Body = body;
            DateTime firstAt = DateTime.MinValue, thinkStart = DateTime.MinValue;
            int usageTokens = 0;
            double engineRate = 0;                       // the engine's own decode speed, from the final chunk's timings
            liveTokens = 0; liveRate = 0;
            rq.OnReasoning = delegate(string d)
            {
                lock (m) { m.Reasoning += d; }
                if (firstAt == DateTime.MinValue) firstAt = DateTime.UtcNow;
                if (thinkStart == DateTime.MinValue) thinkStart = DateTime.UtcNow;
                dirty = true;
            };
            rq.OnContent = delegate(string d)
            {
                if (firstAt == DateTime.MinValue) firstAt = DateTime.UtcNow;
                if (thinkStart != DateTime.MinValue && m.ThinkSecs == null) m.ThinkSecs = (DateTime.UtcNow - thinkStart).TotalSeconds;
                lock (m) { m.Text += d; }
                dirty = true;
            };
            rq.OnUsage = delegate(Dictionary<string, object> u) { double? c = J.Dbl(u, "completion_tokens"); if (c != null) usageTokens = (int)c.Value; };
            rq.OnTimings = delegate(Dictionary<string, object> t) { double? r = J.Dbl(t, "predicted_per_second"); if (r > 0) engineRate = r.Value; };
            running = rq;
            SetBusy(true);
            painter.Start();
            Paint(m, true);
            Thread th = new Thread(delegate()
            {
                string error = rq.Run();
                w.Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (error != null) m.Error = error;
                    m.Stopped = rq.Aborted;
                    if (thinkStart != DateTime.MinValue && m.ThinkSecs == null) m.ThinkSecs = (DateTime.UtcNow - thinkStart).TotalSeconds;
                    if (usageTokens > 0 && firstAt != DateTime.MinValue)
                    {
                        // tokens and speed always go together: the engine's decode speed when the server sent it, otherwise tokens over the time since the first one
                        double secs = (DateTime.UtcNow - firstAt).TotalSeconds;
                        double rate = engineRate > 0 ? engineRate : secs > 0 ? usageTokens / secs : 0;
                        m.Meta = Fmt.N(usageTokens) + " tokens" + (rate > 0 ? " \u00b7 " + Fmt.N(rate, 1) + " tok/s" : "") + (m.Stopped ? " \u00b7 stopped" : "");
                    }
                    else if (m.Stopped) m.Meta = liveTokens > 0 ? Fmt.N(liveTokens) + " tokens" + (liveRate > 0 ? " \u00b7 " + Fmt.N(liveRate, 1) + " tok/s" : "") + " \u00b7 stopped" : "Stopped";
                    if (error != null) w.Toast("error", "The request failed", error, 6000);
                    painter.Stop();
                    dirty = false;
                    running = null;
                    active = null;
                    SetBusy(false);
                    Paint(m, false);
                    ScrollDown(false);
                    SaveChatFile();
                    if (demoDone != null)
                    {
                        Action d = demoDone;
                        demoDone = null;
                        overrideEffort = null;
                        DispatcherTimer t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                        t.Tick += delegate { t.Stop(); d(); };
                        t.Start();
                    }
                }));
            });
            th.IsBackground = true;
            th.Start();
        }

        void SetBusy(bool on)
        {
            busy = on;
            stopBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            sendBtn.IsEnabled = !on;
            hint.Text = on ? "" : "Shift+Enter: new line";
        }

        /// <summary>--screenshot with --prompt: send one message and call back when the answer is complete.</summary>
        public void Demo(string prompt, Action done)
        {
            overrideEffort = "low";
            demoDone = done;
            input.Text = prompt;
            Send();
        }

        // ------------------------------------------------------------------ hooks for --uitest

        public bool Busy { get { return busy; } }

        public int MessageCount { get { return messages.Count; } }
        public string MetaTextOfLast { get { MsgView v; return LastMessage != null && views.TryGetValue(LastMessage, out v) ? v.Meta.Text : ""; } }

        public int ChipCount { get { return attachments.Count; } }

        public ChatMsg LastMessage { get { return messages.Count > 0 ? messages[messages.Count - 1] : null; } }

        public void TestSend(string text) { input.Text = text; Send(); }

        public void StopForTest() { if (running != null) running.Abort(); }

        public void NewChatForTest() { NewChat(); }

        public void UndoForTest() { if (undoBackup == null) return; messages.Clear(); messages.AddRange(undoBackup); undoBackup = null; RenderAll(); SaveChatFile(); }

        public void ClearForTest() { messages.Clear(); undoBackup = null; RenderAll(); SaveChatFile(); }

        public void AttachForTest(string path) { AddFiles(new string[] { path }); }

        // ------------------------------------------------------------------ new chat, save, attachments

        void NewChat()
        {
            if (busy) { w.Toast("warn", "Still writing", "Stop the answer first.", 3500); return; }
            if (messages.Count == 0) return;
            undoBackup = new List<ChatMsg>(messages);
            messages.Clear();
            RenderAll();
            SaveChatFile();
            w.Toast("info", "New chat", "The last one was cleared.", 6000, "Undo", delegate
            {
                if (undoBackup == null) return;
                messages.Clear(); messages.AddRange(undoBackup); undoBackup = null;
                RenderAll(); SaveChatFile();
            });
        }

        void SaveChat()
        {
            if (messages.Count == 0) { w.Toast("info", "Nothing to save yet", "", 2500); return; }
            Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.Filter = "Markdown (*.md)|*.md";
            dlg.FileName = "strata-chat-" + DateTime.Now.ToString("yyyy-MM-dd-HH-mm") + ".md";
            if (dlg.ShowDialog(w) != true) return;
            StringBuilder sb = new StringBuilder();
            foreach (ChatMsg m in messages)
            {
                if (m.Role == "user") { sb.Append("## You\n\n").Append(m.Text).Append("\n\n"); continue; }
                sb.Append("## ").Append(ModelName()).Append("\n\n");
                if (m.Reasoning.Length > 0) sb.Append("<details><summary>Thinking</summary>\n\n").Append(m.Reasoning).Append("\n\n</details>\n\n");
                sb.Append(m.Text.Length > 0 ? m.Text : m.Error).Append("\n\n");
            }
            try { File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false)); w.Toast("success", "Chat saved", Path.GetFileName(dlg.FileName), 3500); }
            catch (Exception ex) { w.Toast("error", "Not saved", ex.Message, 5000); }
        }

        void PickFiles()
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Multiselect = true;
            dlg.Filter = "Text and code files|*.*";
            if (dlg.ShowDialog(w) == true) AddFiles(dlg.FileNames);
        }

        void AddFiles(string[] files)
        {
            foreach (string f in files)
            {
                try
                {
                    FileInfo fi = new FileInfo(f);
                    string name = fi.Name;
                    if (!TextExt.IsMatch(name) && !Regex.IsMatch(name, @"^(makefile|dockerfile|readme|license)$", RegexOptions.IgnoreCase))
                    { w.Toast("warn", "Not a text file", name + ": attach text files (code, notes, logs, data).", 5000); continue; }
                    if (fi.Length > 512 * 1024) { w.Toast("warn", "File too large", name + " is over 512 KB.", 5000); continue; }
                    string text = File.ReadAllText(f);
                    if (text.IndexOf('\0') >= 0) { w.Toast("warn", "Not a text file", name + " looks like a binary file.", 5000); continue; }
                    Attachment a = new Attachment();
                    a.Name = name; a.Text = text;
                    attachments.Add(a);
                }
                catch (Exception ex) { w.Toast("error", "Could not read the file", ex.Message, 5000); }
            }
            RenderChips();
        }

        void RenderChips()
        {
            chips.Children.Clear();
            chips.Visibility = attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (Attachment a in new List<Attachment>(attachments))
            {
                Attachment captured = a;
                chips.Children.Add(Chip(a.Name, delegate { attachments.Remove(captured); RenderChips(); }));
            }
        }

        // ------------------------------------------------------------------ the chat survives a restart (text only)

        static string ChatPath { get { return Path.Combine(Paths.Roaming, "chat.json"); } }

        void SaveChatFile()
        {
            try
            {
                List<object> o = new List<object>();
                foreach (ChatMsg m in messages)
                {
                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["role"] = m.Role; d["text"] = m.Text; d["reasoning"] = m.Reasoning; d["meta"] = m.Meta; d["error"] = m.Error;
                    d["time"] = m.Time.ToString("o"); d["thinkSecs"] = m.ThinkSecs; d["stopped"] = m.Stopped;
                    List<object> files = new List<object>();
                    foreach (Attachment f in m.Files) { Dictionary<string, object> fd = new Dictionary<string, object>(); fd["name"] = f.Name; fd["text"] = f.Text; files.Add(fd); }
                    d["files"] = files;
                    o.Add(d);
                }
                Directory.CreateDirectory(Paths.Roaming);
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                File.WriteAllText(ChatPath, js.Serialize(o));
            }
            catch (Exception ex) { Paths.Diag("chat not saved: " + ex.Message); }
        }

        void LoadChat()
        {
            try
            {
                if (!File.Exists(ChatPath)) return;
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                IList l = js.DeserializeObject(File.ReadAllText(ChatPath)) as IList;
                if (l == null) return;
                foreach (object x in l)
                {
                    ChatMsg m = new ChatMsg();
                    m.Role = J.Str(x, "role") ?? "user";
                    m.Text = J.Str(x, "text") ?? ""; m.Reasoning = J.Str(x, "reasoning") ?? ""; m.Meta = J.Str(x, "meta") ?? ""; m.Error = J.Str(x, "error") ?? "";
                    DateTime t;
                    if (DateTime.TryParse(J.Str(x, "time"), null, System.Globalization.DateTimeStyles.RoundtripKind, out t)) m.Time = t;
                    m.ThinkSecs = J.Dbl(x, "thinkSecs");
                    m.Stopped = J.Bool(x, "stopped");
                    foreach (object f in J.List(x, "files")) { Attachment a = new Attachment(); a.Name = J.Str(f, "name") ?? ""; a.Text = J.Str(f, "text") ?? ""; m.Files.Add(a); }
                    messages.Add(m);
                }
            }
            catch (Exception ex) { Paths.Diag("chat not loaded: " + ex.Message); }
        }
    }
}
