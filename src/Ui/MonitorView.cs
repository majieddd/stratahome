using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace StrataHome
{
    /// <summary>
    /// The Monitor tab: the web app's Monitor (serve/web/app.js renderMonitor) drawn natively from the same /metrics data.
    /// </summary>
    internal sealed class MonitorView
    {
        public readonly FrameworkElement Root;
        readonly MainWindow w;

        sealed class Tile
        {
            public TextBlock Value, Unit, Sub, Value2, Unit2, Sub2;
            public Sparkline Spark;
        }

        readonly Dictionary<string, Tile> tiles = new Dictionary<string, Tile>();
        readonly Dictionary<string, Border> badges = new Dictionary<string, Border>();
        TextBlock stateLabel, stateDetail, ctxPct, ctxSub, slotsText, ramText, tempText, totals, ccSum, ccNote;
        BarView stateBar, slotsBar, ramBar, tempBar, ccSlotsBar, ccMemBar;
        GaugeView gauge;
        Grid metricGrid, rowGrid, table, ccBars;
        Border ccCard;
        Button showAll;
        StackPanel ccFactsHost;
        bool wide = true;

        static readonly string[][] Metrics = new string[][]
        {
            // key, label, icon, tone
            new string[] { "speed", "Speed", "gauge", "accent" },
            new string[] { "gpu", "GPU load", "gpu", "accent" },
            new string[] { "vram", "VRAM", "layers", "accent" },
            new string[] { "temp", "GPU temp", "thermometer", "warn" },
            new string[] { "power", "Power", "bolt", "accent" },
            new string[] { "pcie", "PCIe", "link", "info" },
            new string[] { "cpu", "CPU", "cpu", "accent" },
            new string[] { "disk", "Disk read", "disk", "info" }
        };

        public MonitorView(MainWindow window)
        {
            w = window;
            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            StackPanel col = new StackPanel();
            col.MaxWidth = 1240;
            col.Margin = new Thickness(24);
            col.HorizontalAlignment = HorizontalAlignment.Stretch;

            col.Children.Add(BuildStateCard());
            col.Children.Add(Gap(20));
            col.Children.Add(BuildMetrics());
            col.Children.Add(Gap(20));
            col.Children.Add(BuildRow());
            col.Children.Add(Gap(20));
            col.Children.Add(BuildCacheCard());
            sv.Content = col;
            Root = sv;
            sv.SizeChanged += delegate { Layout(sv.ActualWidth); };
        }

        static FrameworkElement Gap(double h) { return new Border { Height = h }; }

        static TextBlock Title(string s) { return Ui.Text(s, 16, "StInk", "bold"); }

        // ------------------------------------------------------------------ state card

        FrameworkElement BuildStateCard()
        {
            StackPanel sp = new StackPanel();
            WrapPanel head = new WrapPanel();
            TextBlock t = Title("Model state");
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(0, 0, 16, 0);
            head.Children.Add(t);
            string[][] list = new string[][]
            {
                new string[] { "idle", "Idle", "idle" }, new string[] { "reading", "Reading", "reading" },
                new string[] { "generating", "Generating", "generating" }, new string[] { "queued", "Queued", "queued" },
                new string[] { "error", "Error", "error" }
            };
            foreach (string[] b in list)
            {
                Border badge = Ui.Badge(b[1], b[2]);
                badge.Margin = new Thickness(0, 0, 8, 0);
                badge.Opacity = 0.38;
                badges[b[0]] = badge;
                head.Children.Add(badge);
            }
            sp.Children.Add(head);

            Grid row = new Grid();
            row.Margin = new Thickness(0, 12, 0, 12);
            row.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            row.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            stateLabel = Ui.Text("Waiting for a request", 13, "StInk", "medium");
            stateDetail = Ui.Text("", 13, "StInkMuted", "medium");
            row.Children.Add(Ui.At(stateLabel, 0, 0));
            row.Children.Add(Ui.At(stateDetail, 0, 1));
            sp.Children.Add(row);
            stateBar = new BarView();
            sp.Children.Add(stateBar);
            return Ui.Card(sp);
        }

        // ------------------------------------------------------------------ the eight tiles

        FrameworkElement BuildMetrics()
        {
            metricGrid = new Grid();
            foreach (string[] m in Metrics)
            {
                Tile tile = new Tile();
                tiles[m[0]] = tile;
                StackPanel sp = new StackPanel();

                StackPanel label = new StackPanel();
                label.Orientation = Orientation.Horizontal;
                IconView iv = new IconView(m[2], 16);
                iv.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "StInkMuted");
                iv.VerticalAlignment = VerticalAlignment.Center;
                iv.Margin = new Thickness(0, 0, 8, 0);
                label.Children.Add(iv);
                label.Children.Add(Ui.Text(m[1], 13, "StInkMuted", "medium"));
                sp.Children.Add(label);

                if (m[0] == "speed")
                {
                    Grid two = new Grid();
                    two.Margin = new Thickness(0, 10, 0, 0);
                    two.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
                    two.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
                    StackPanel dec = Value(tile, false);
                    StackPanel pre = Value(tile, true);
                    pre.HorizontalAlignment = HorizontalAlignment.Right;
                    two.Children.Add(Ui.At(dec, 0, 0));
                    two.Children.Add(Ui.At(pre, 0, 1));
                    sp.Children.Add(two);
                }
                else
                {
                    StackPanel v = Value(tile, false);
                    v.Margin = new Thickness(0, 10, 0, 0);
                    sp.Children.Add(v);
                }
                tile.Spark = new Sparkline(m[3]);
                tile.Spark.Margin = new Thickness(0, 10, 0, 0);
                sp.Children.Add(tile.Spark);
                Border card = Ui.Card(sp);
                card.Padding = new Thickness(20, 16, 20, 16);
                card.Margin = new Thickness(8);
                metricGrid.Children.Add(card);
            }
            metricGrid.Margin = new Thickness(-8);
            return metricGrid;
        }

        /// <summary>Value + unit on one line, the small sub line under it.</summary>
        StackPanel Value(Tile t, bool second)
        {
            StackPanel sp = new StackPanel();
            StackPanel line = new StackPanel();
            line.Orientation = Orientation.Horizontal;
            TextBlock v = Ui.Text("\u2013", 32, "StInk", "black");
            TextBlock u = Ui.Text("", 14, "StInkMuted", "medium");
            u.Margin = new Thickness(4, 0, 0, 0);
            u.VerticalAlignment = VerticalAlignment.Bottom;
            u.Padding = new Thickness(0, 0, 0, 5);
            line.Children.Add(v); line.Children.Add(u);
            TextBlock sub = Ui.Text("", 12, "StInkMuted");
            sub.Margin = new Thickness(0, 10, 0, 0);
            sub.MinHeight = 14;
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(line); sp.Children.Add(sub);
            if (second) { t.Value2 = v; t.Unit2 = u; t.Sub2 = sub; }
            else { t.Value = v; t.Unit = u; t.Sub = sub; }
            return sp;
        }

        // ------------------------------------------------------------------ context gauge + requests

        FrameworkElement BuildRow()
        {
            rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            rowGrid.ColumnDefinitions.Add(Ui.Col(Ui.Star(2.4)));

            StackPanel ctx = new StackPanel();
            ctx.Children.Add(Title("Context fill"));
            Grid gg = new Grid { Width = 150, Height = 150, Margin = new Thickness(0, 8, 0, 12), HorizontalAlignment = HorizontalAlignment.Center };
            gauge = new GaugeView(150);
            gg.Children.Add(gauge);
            StackPanel lab = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            ctxPct = Ui.Text("0%", 24, "StInk", "black");
            ctxPct.HorizontalAlignment = HorizontalAlignment.Center;
            ctxSub = Ui.Text("\u2013", 12, "StInkMuted");
            ctxSub.HorizontalAlignment = HorizontalAlignment.Center;
            lab.Children.Add(ctxPct); lab.Children.Add(ctxSub);
            gg.Children.Add(lab);
            ctx.Children.Add(gg);
            slotsText = BarRow(ctx, "Experts in VRAM", out slotsBar);
            ramText = BarRow(ctx, "System RAM", out ramBar);
            tempText = BarRow(ctx, "GPU temperature", out tempBar);
            Border ctxCard = Ui.Card(ctx);
            ctxCard.Margin = new Thickness(0, 0, 8, 0);
            rowGrid.Children.Add(Ui.At(ctxCard, 0, 0));

            StackPanel req = new StackPanel();
            Grid head = new Grid();
            head.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            head.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            head.Children.Add(Ui.At(Title("Recent requests"), 0, 0));
            showAll = Ui.Btn("secondary", "Show all", null, delegate
            {
                w.Metrics.ShowAll = !w.Metrics.ShowAll;
                Render(w.Metrics.Last);
            });
            showAll.Height = 32; showAll.Padding = new Thickness(12, 0, 12, 0); showAll.FontSize = 13;
            showAll.Visibility = Visibility.Collapsed;
            head.Children.Add(Ui.At(showAll, 0, 1));
            req.Children.Add(head);
            table = new Grid();
            table.Margin = new Thickness(0, 12, 0, 0);
            string[] widths = new string[] { "1.3", "1.5", "1", "1", "1", "0.9", "2.2", "1.1" };
            foreach (string wd in widths) table.ColumnDefinitions.Add(Ui.Col(Ui.Star(double.Parse(wd, System.Globalization.CultureInfo.InvariantCulture))));
            ScrollViewer tsv = new ScrollViewer();
            tsv.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            tsv.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            tsv.Content = table;
            req.Children.Add(tsv);
            totals = Ui.Text("", 13, "StInkMuted", "regular", true);
            totals.Margin = new Thickness(0, 12, 0, 0);
            totals.Visibility = Visibility.Collapsed;
            req.Children.Add(totals);
            Border reqCard = Ui.Card(req);
            reqCard.Margin = new Thickness(8, 0, 0, 0);
            rowGrid.Children.Add(Ui.At(reqCard, 0, 1));
            return rowGrid;
        }

        TextBlock BarRow(StackPanel host, string label, out BarView bar)
        {
            Grid row = new Grid();
            row.Margin = new Thickness(0, 8, 0, 8);
            row.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            row.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            row.Children.Add(Ui.At(Ui.Text(label, 13, "StInk", "medium"), 0, 0));
            TextBlock val = Ui.Text("\u2013", 13, "StInkMuted", "medium");
            row.Children.Add(Ui.At(val, 0, 1));
            host.Children.Add(row);
            bar = new BarView();
            host.Children.Add(bar);
            return val;
        }

        // ------------------------------------------------------------------ conversation cache

        FrameworkElement BuildCacheCard()
        {
            StackPanel sp = new StackPanel();
            Grid head = new Grid();
            head.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            head.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            head.Children.Add(Ui.At(Title("Conversation cache"), 0, 0));
            ccSum = Ui.Text("", 12, "StInkMuted");
            head.Children.Add(Ui.At(ccSum, 0, 1));
            sp.Children.Add(head);
            ccBars = new Grid();
            ccBars.Margin = new Thickness(0, 12, 0, 0);
            ccBars.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            ccBars.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            StackPanel a = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            BarRow(a, "Parked conversations", out ccSlotsBar);
            StackPanel b = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            BarRow(b, "Their memory (RAM)", out ccMemBar);
            ccBars.Children.Add(Ui.At(a, 0, 0));
            ccBars.Children.Add(Ui.At(b, 0, 1));
            ccBars.Visibility = Visibility.Collapsed;
            sp.Children.Add(ccBars);
            ccFactsHost = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            sp.Children.Add(ccFactsHost);
            ccNote = Ui.Text("", 12, "StInkMuted", "regular", true);
            ccNote.Margin = new Thickness(0, 12, 0, 0);
            sp.Children.Add(ccNote);
            ccCard = Ui.Card(sp);
            ccCard.Visibility = Visibility.Collapsed;
            return ccCard;
        }

        // ------------------------------------------------------------------ responsive layout (the web app's 1000 px breakpoint)

        void Layout(double width)
        {
            bool nowWide = width >= 1000;
            // the tile grid: 4 columns wide, 2 narrow
            int cols = nowWide ? 4 : 2;
            metricGrid.ColumnDefinitions.Clear();
            metricGrid.RowDefinitions.Clear();
            for (int c = 0; c < cols; c++) metricGrid.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            for (int r = 0; r < Metrics.Length / cols; r++) metricGrid.RowDefinitions.Add(Ui.Row(Ui.Auto));
            for (int i = 0; i < metricGrid.Children.Count; i++) { Grid.SetRow(metricGrid.Children[i], i / cols); Grid.SetColumn(metricGrid.Children[i], i % cols); }
            if (nowWide != wide)
            {
                rowGrid.ColumnDefinitions.Clear(); rowGrid.RowDefinitions.Clear();
                if (nowWide)
                {
                    rowGrid.ColumnDefinitions.Add(Ui.Col(Ui.Star(1))); rowGrid.ColumnDefinitions.Add(Ui.Col(Ui.Star(2.4)));
                    Grid.SetRow(rowGrid.Children[0], 0); Grid.SetColumn(rowGrid.Children[0], 0);
                    Grid.SetRow(rowGrid.Children[1], 0); Grid.SetColumn(rowGrid.Children[1], 1);
                    ((FrameworkElement)rowGrid.Children[0]).Margin = new Thickness(0, 0, 8, 0);
                    ((FrameworkElement)rowGrid.Children[1]).Margin = new Thickness(8, 0, 0, 0);
                }
                else
                {
                    rowGrid.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
                    rowGrid.RowDefinitions.Add(Ui.Row(Ui.Auto)); rowGrid.RowDefinitions.Add(Ui.Row(Ui.Auto));
                    Grid.SetRow(rowGrid.Children[0], 0); Grid.SetColumn(rowGrid.Children[0], 0);
                    Grid.SetRow(rowGrid.Children[1], 1); Grid.SetColumn(rowGrid.Children[1], 0);
                    ((FrameworkElement)rowGrid.Children[0]).Margin = new Thickness(0, 0, 0, 16);
                    ((FrameworkElement)rowGrid.Children[1]).Margin = new Thickness(0);
                }
                wide = nowWide;
            }
        }

        // ------------------------------------------------------------------ rendering

        void SetTile(string key, string value, string unit, string sub)
        {
            Tile t = tiles[key];
            t.Value.Text = value ?? "\u2013";
            t.Unit.Text = value == null ? "" : (unit ?? "");
            t.Sub.Text = sub ?? "";
        }

        public void Render(Dictionary<string, object> m)
        {
            if (m == null) return;
            object live = J.Get(m, "live"), hw = J.Get(m, "hardware"), st = J.Get(m, "hardware_static"), eng = J.Get(m, "engine"), h = J.Get(m, "history");
            IList reqs = J.List(m, "requests");
            object last = reqs.Count > 0 ? reqs[0] : null;
            string state = J.Str(live, "state") ?? "idle";
            double queued = J.Dbl(live, "queued") ?? 0;

            // model state
            string on = queued > 0 ? "queued" : state;
            foreach (KeyValuePair<string, Border> kv in badges) kv.Value.Opacity = (kv.Key == on || kv.Key == state) ? 1.0 : 0.38;
            string label = "Waiting for a request", detail = "";
            double pct = 0;
            string tone = "accent";
            if (state == "reading")
            {
                label = "Reading prompt"; tone = "info";
                double? total = J.Dbl(live, "prompt_total"), read = J.Dbl(live, "prompt_read");
                if (total > 0) { pct = 100 * (read ?? 0) / total.Value; detail = Fmt.N(read) + " / " + Fmt.N(total) + " tokens \u00b7 " + Fmt.N(pct) + "%"; }
                else detail = Fmt.N(J.Dbl(live, "prompt_tokens")) + " tokens";
            }
            else if (state == "generating")
            {
                string phase = J.Str(live, "phase");
                label = string.IsNullOrEmpty(phase) ? "Generating" : char.ToUpper(phase[0]) + phase.Substring(1);
                double? max = J.Dbl(live, "max_tokens");
                pct = max > 0 ? Math.Min(100, 100 * (J.Dbl(live, "generated") ?? 0) / max.Value) : 0;
                detail = Fmt.N(J.Dbl(live, "generated")) + " tokens \u00b7 " + Fmt.N(J.Dbl(live, "tok_s"), 1) + " tok/s";
            }
            else if (last != null)
            {
                double? ts = J.Dbl(last, "decode_tok_s");
                detail = "last: " + Fmt.N(J.Dbl(last, "output_tokens")) + " tokens" + (ts > 0 ? " at " + Fmt.N(ts, 1) + " tok/s" : "");
            }
            stateLabel.Text = label;
            stateDetail.Text = detail;
            stateBar.Set(pct / 100.0, tone);

            // the eight tiles
            double? speed = state == "generating" ? J.Dbl(live, "tok_s") : last != null ? J.Dbl(last, "decode_tok_s") : null;
            SetTile("speed", speed == null ? null : Fmt.N(speed, 1), "t/s", state == "generating" ? "Decode now" : last != null ? "Decode last request" : "Decode");
            double? prefill = state != "idle" ? J.Dbl(live, "prefill_tok_s_mean")
                : (last != null && J.Dbl(last, "prompt_ms") > 0)
                    ? Math.Max(0, (J.Dbl(last, "prompt_tokens") ?? 0) - (J.Dbl(last, "reused") ?? 0)) / (J.Dbl(last, "prompt_ms").Value / 1000.0) : (double?)null;
            Tile sp = tiles["speed"];
            sp.Value2.Text = prefill == null ? "\u2013" : Fmt.N(prefill);
            sp.Unit2.Text = prefill == null ? "" : "t/s";
            sp.Sub2.Text = state == "reading" ? "Prefill now" : state == "generating" ? "Prefill this request" : last != null ? "Prefill last request" : "Prefill";
            sp.Spark.Set(J.Series(h, "tok_s"), J.Series(h, "prefill_tok_s_mean"), 0);

            IList gpus = J.List(hw, "gpus");
            bool multi = gpus.Count > 1;
            Func<string, string> per = delegate(string f)
            {
                List<string> parts = new List<string>();
                foreach (object g in gpus)
                {
                    double? x = J.Dbl(g, f);
                    string val = x == null ? "\u2013" : f == "mem_used" ? Fmt.GB(x) + " GB" : f == "util" ? Fmt.N(x) + "%" : Fmt.N(x) + "\u00b0";
                    parts.Add("GPU " + J.Str(g, "index") + " " + val);
                }
                return string.Join(" \u00b7 ", parts.ToArray());
            };
            double? util = J.Dbl(hw, "gpu_util");
            SetTile("gpu", util == null ? null : Fmt.N(util), "%", multi ? per("util") : J.Str(st, "gpu_name"));
            tiles["gpu"].Spark.Set(J.Series(h, "gpu_util"), null, 100);

            double? memUsed = J.Dbl(hw, "gpu_mem_used"), memTotal = J.Dbl(hw, "gpu_mem_total");
            double? slots = J.Dbl(eng, "expert_slots");
            SetTile("vram", memUsed == null ? null : Fmt.GB(memUsed), memTotal > 0 ? "/ " + Fmt.GB(memTotal, 0) + " GB" : "GB",
                    multi ? per("mem_used") : slots > 0 ? Fmt.N(slots) + " experts cached" : "");
            tiles["vram"].Spark.Set(J.Series(h, "gpu_mem_used"), null, memTotal ?? 0);

            double? temp = J.Dbl(hw, "gpu_temp");
            SetTile("temp", temp == null ? null : Fmt.N(temp), "\u00b0C", multi ? per("temp") : "");
            tiles["temp"].Spark.Set(J.Series(h, "gpu_temp"), null, 90);

            double? power = J.Dbl(hw, "gpu_power"), limit = J.Dbl(hw, "gpu_power_limit");
            SetTile("power", power == null ? null : Fmt.N(power), "W", limit > 0 ? "of " + Fmt.N(limit) + " W limit" : "");
            tiles["power"].Spark.Set(J.Series(h, "gpu_power"), null, limit ?? 0);

            double? gen = J.Dbl(hw, "gpu_pcie_gen_max") ?? J.Dbl(hw, "gpu_pcie_gen");
            double? width = J.Dbl(hw, "gpu_pcie_width"), rx = J.Dbl(hw, "gpu_pcie_rx_mb"), cur = J.Dbl(hw, "gpu_pcie_gen");
            string pcieSub = rx == null ? "" : "to GPU " + Fmt.N(rx, rx < 10 ? 1 : 0) + " MB/s" + (cur != null && gen != null && cur < gen ? " \u00b7 idle Gen" + (int)cur.Value : "");
            SetTile("pcie", gen > 0 ? "Gen" + (int)gen.Value : null, width > 0 ? "x" + (int)width.Value : "", pcieSub);
            tiles["pcie"].Spark.Set(J.Series(h, "gpu_pcie_rx_mb"), null, 0);

            double? cpu = J.Dbl(hw, "cpu"), threads = J.Dbl(st, "threads"), cores = J.Dbl(st, "cores");
            SetTile("cpu", cpu == null ? null : Fmt.N(cpu), "%", threads > 0 ? (cores > 0 ? Fmt.N(cores) + " cores \u00b7 " : "") + Fmt.N(threads) + " threads" : "");
            tiles["cpu"].Spark.Set(J.Series(h, "cpu"), null, 100);

            double? disk = J.Dbl(hw, "disk_read_mb"), diskW = J.Dbl(hw, "disk_write_mb");
            if (disk == null) SetTile("disk", null, "", J.Bool(st, "psutil") ? "" : "needs psutil (setup installs it)");
            else
            {
                bool big = disk >= 1000;
                SetTile("disk", big ? Fmt.N(disk / 1024, 2) : Fmt.N(disk, disk < 10 ? 1 : 0), big ? "GB/s" : "MB/s", diskW == null ? "" : "write " + Fmt.N(diskW, 1) + " MB/s");
            }
            tiles["disk"].Spark.Set(J.Series(h, "disk_read_mb"), null, 0);

            // context fill: the running request, else the last one
            double ctx = J.Dbl(eng, "max_context") ?? 0;
            double used = 0;
            if (state != "idle") used = (J.Dbl(live, "prompt_tokens") ?? 0) + (J.Dbl(live, "generated") ?? 0);
            else if (last != null) used = (J.Dbl(last, "prompt_tokens") ?? 0) + (J.Dbl(last, "output_tokens") ?? 0);
            double frac = ctx > 0 ? Math.Min(1, used / ctx) : 0;
            gauge.Set(frac);
            ctxPct.Text = Math.Round(frac * 100) + "%";
            ctxSub.Text = ctx > 0 ? Fmt.K(used) + " / " + Fmt.Ctx(ctx) : "\u2013";
            double cacheBytes = (J.Dbl(eng, "expert_cache_mib") ?? 0) * 1048576;
            slotsText.Text = slots > 0 ? Fmt.N(slots) + " \u00b7 " + Fmt.GB(cacheBytes) + " GB" : "\u2013";
            slotsBar.Set(memTotal > 0 ? cacheBytes / memTotal.Value : 0, "accent");
            double? ramTotal = J.Dbl(hw, "ram_total"), ramUsed = J.Dbl(hw, "ram_used");
            ramText.Text = ramTotal > 0 ? Fmt.GB(ramUsed) + " / " + Fmt.GB(ramTotal, 0) + " GB" : "\u2013";
            double ramPct = ramTotal > 0 ? 100 * (ramUsed ?? 0) / ramTotal.Value : 0;
            ramBar.Set(ramPct / 100, ramPct > 92 ? "danger" : "accent");
            tempText.Text = temp == null ? "\u2013" : Fmt.N(temp) + " \u00b0C";
            tempBar.Set(temp == null ? 0 : Math.Min(100, temp.Value) / 100, "warn");

            RenderRequests(reqs, m);
            RenderCache(J.Get(m, "conversation_cache"));
        }

        /// <summary>For --uitest: what a tile currently shows.</summary>
        public string Probe(string key) { Tile t = tiles[key]; return t.Value.Text + " " + t.Unit.Text; }

        public string ProbeState() { return stateLabel.Text + " | " + stateDetail.Text; }

        void RenderRequests(IList reqs, Dictionary<string, object> m)
        {
            table.Children.Clear();
            table.RowDefinitions.Clear();
            string[] heads = new string[] { "Time", "Status", "Prompt", "Reused", "Output", "Tok/s", "VRAM hit rate", "Duration" };
            table.RowDefinitions.Add(Ui.Row(Ui.Auto));
            for (int c = 0; c < heads.Length; c++)
            {
                TextBlock t = Ui.Text(heads[c].ToUpperInvariant(), 12, "StInkMuted", "medium");
                t.Margin = new Thickness(12, 10, 12, 10);
                if (c >= 2) t.HorizontalAlignment = HorizontalAlignment.Right;
                Border cell = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
                cell.SetResourceReference(Border.BorderBrushProperty, "StLine");
                cell.Child = t;
                table.Children.Add(Ui.At(cell, 0, c));
            }
            if (reqs.Count == 0)
            {
                table.RowDefinitions.Add(Ui.Row(Ui.Auto));
                TextBlock t = Ui.Text("No requests yet", 14, "StInkMuted");
                t.Margin = new Thickness(12, 12, 12, 12);
                Grid.SetColumnSpan(t, 8);
                table.Children.Add(Ui.At(t, 1, 0));
            }
            else
            {
                bool all = w.Metrics.ShowAll;
                int n = all ? reqs.Count : Math.Min(12, reqs.Count);
                for (int i = 0; i < n; i++)
                {
                    object r = reqs[i];
                    int row = i + 1;
                    table.RowDefinitions.Add(Ui.Row(Ui.Auto));
                    string finish = J.Str(r, "finish");
                    string text = finish == "stop" ? "Done" : finish == "length" ? "Max tokens" : finish == "cancel" ? "Stopped" : finish == "disconnect" ? "Closed" : finish == "error" ? "Error" : (finish ?? "\u2013");
                    string kind = finish == "cancel" || finish == "disconnect" ? "queued" : finish == "error" ? "error" : "idle";
                    double? time = J.Dbl(r, "time");
                    string when = time == null ? "\u2013" : new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(time.Value).ToLocalTime().ToString("T");
                    StackPanel status = new StackPanel { Orientation = Orientation.Horizontal };
                    status.Children.Add(Ui.Badge(text, kind));
                    object proj = J.Get(r, "projection");
                    if (proj != null)
                    {
                        bool on = proj is bool && (bool)proj;
                        Border pb = Ui.Badge(on ? "ESP" : "stock", on ? "reading" : "idle");
                        pb.Margin = new Thickness(6, 0, 0, 0);
                        status.Children.Add(pb);
                    }
                    double? hit = J.Dbl(r, "hit_rate"), pcie = J.Dbl(r, "pcie_share");
                    StackPanel hitCell = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                    hitCell.Children.Add(Ui.Text(hit == null ? "\u2013" : (hit.Value * 100).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + "%", 14, "StInkSoft"));
                    if (pcie > 0)
                    {
                        TextBlock pt = Ui.Text(" +" + (pcie.Value * 100).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + "% PCIe", 14, "StInkMuted");
                        hitCell.Children.Add(pt);
                    }
                    UIElement[] cells = new UIElement[]
                    {
                        Ui.Text(when, 14, "StInkSoft"), status,
                        Num(Fmt.N(J.Dbl(r, "prompt_tokens"))), Num(Fmt.N(J.Dbl(r, "reused"))), Num(Fmt.N(J.Dbl(r, "output_tokens"))),
                        Num(Fmt.N(J.Dbl(r, "decode_tok_s"), 1)), hitCell, Num(Fmt.N(J.Dbl(r, "duration_s"), 1) + " s")
                    };
                    for (int c = 0; c < cells.Length; c++)
                    {
                        FrameworkElement fe = (FrameworkElement)cells[c];
                        fe.VerticalAlignment = VerticalAlignment.Center;
                        Border cell = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 12, 12, 12) };
                        cell.SetResourceReference(Border.BorderBrushProperty, "StLineSoft");
                        cell.Child = fe;
                        table.Children.Add(Ui.At(cell, row, c));
                    }
                }
            }
            double? kept = J.Dbl(m, "requests_kept");
            showAll.Visibility = (kept ?? reqs.Count) > 12 ? Visibility.Visible : Visibility.Collapsed;
            showAll.Content = w.Metrics.ShowAll ? "Show fewer" : "Show all (" + Fmt.N(kept ?? reqs.Count) + ")";
            string tot = TotalsText(J.Get(m, "totals"));
            totals.Text = tot;
            totals.Visibility = tot.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        static TextBlock Num(string s)
        {
            TextBlock t = Ui.Text(s, 14, "StInkSoft");
            t.HorizontalAlignment = HorizontalAlignment.Right;
            return t;
        }

        static string TotalsText(object t)
        {
            double? requests = J.Dbl(t, "requests");
            if (requests == null || requests == 0) return "";
            double since = J.Dbl(t, "since") ?? 0;
            string when = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(since).ToLocalTime().ToString("ddd HH:mm");
            double read = (J.Dbl(t, "prompt_tokens") ?? 0) - (J.Dbl(t, "reused") ?? 0);
            double promptMs = J.Dbl(t, "prompt_ms") ?? 0, decodeMs = J.Dbl(t, "decode_ms") ?? 0, outTokens = J.Dbl(t, "output_tokens") ?? 0;
            string pSpeed = promptMs > 0 && read > 0 ? " at " + Fmt.N(read / (promptMs / 1000)) + " tok/s" : "";
            string oSpeed = decodeMs > 0 && outTokens > 0 ? " at " + Fmt.N(outTokens / (decodeMs / 1000), 1) + " tok/s" : "";
            return "Since " + when + ": " + Fmt.N(requests) + " requests \u00b7 " + Fmt.N(read) + " prompt tokens read" + pSpeed + " (" + Fmt.N(J.Dbl(t, "reused")) +
                   " reused) \u00b7 " + Fmt.N(outTokens) + " written" + oSpeed;
        }

        void RenderCache(object c)
        {
            ccCard.Visibility = c == null ? Visibility.Collapsed : Visibility.Visible;
            if (c == null) return;
            bool enabled = J.Bool(c, "enabled");
            ccBars.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (enabled)
            {
                double slots = J.Dbl(c, "slots") ?? 0, parked = J.Dbl(c, "parked") ?? 0;
                SetBarRow(ccSlotsBar, Fmt.N(parked) + " / " + Fmt.N(slots), slots > 0 ? parked / slots : 0);
                double budget = (J.Dbl(c, "budget_mib") ?? 0) * 1048576, bytes = J.Dbl(c, "bytes") ?? 0;
                SetBarRow(ccMemBar, Fmt.GB(bytes) + " / " + Fmt.GB(budget) + " GB", budget > 0 ? bytes / budget : 0);
            }
            double? req = J.Dbl(c, "requests");
            ccSum.Text = req > 0 ? Fmt.N(J.Dbl(c, "requests_reused")) + " of " + Fmt.N(req) + " requests reused part of their prompt" : "";
            double promptTokens = J.Dbl(c, "prompt_tokens") ?? 0, reused = J.Dbl(c, "reused_tokens") ?? 0;
            string share = promptTokens > 0 ? " (" + Fmt.N(100 * reused / promptTokens) + "% of all prompt tokens)" : "";
            string lastEvent = J.Str(c, "last_event");
            string ev = lastEvent == null ? null : (lastEvent == "parked" ? "Parked " : "Restored ") + Fmt.N(J.Dbl(c, "last_tokens")) + " tokens, " + Since(J.Dbl(c, "last_at"));
            List<string[]> rows = new List<string[]>();
            if (J.Dbl(c, "last_prompt") != null) rows.Add(new string[] { "Last request", Fmt.N(J.Dbl(c, "last_reused") ?? 0) + " of " + Fmt.N(J.Dbl(c, "last_prompt")) + " prompt tokens reused" });
            if (req > 0) rows.Add(new string[] { "Reused since start", Fmt.N(reused) + " tokens" + share });
            if (enabled)
            {
                double ev2 = J.Dbl(c, "evictions") ?? 0;
                rows.Add(new string[] { "Parked / restored", Fmt.N(J.Dbl(c, "parks")) + " / " + Fmt.N(J.Dbl(c, "restores")) + (ev2 > 0 ? " \u00b7 " + Fmt.N(ev2) + " evicted" : "") });
                if (ev != null) rows.Add(new string[] { "Last switch", ev });
            }
            ccFactsHost.Children.Clear();
            ccFactsHost.Children.Add(Ui.Facts(rows));
            ccNote.Text = enabled
                ? "A request that continues a parked conversation gets its state back instead of reading it again; the oldest goes when the slots or the memory are full."
                : "The engine keeps the last conversation's state, so a follow-up reads only what is new. To keep several conversations (agents taking turns), add \"--conversation-cache-mib\", \"8192\" to the run config's args (docs/DETAILS.md).";
        }

        void SetBarRow(BarView bar, string text, double frac)
        {
            bar.Set(frac, "accent");
            Grid row = (Grid)((StackPanel)bar.Parent).Children[0];
            ((TextBlock)row.Children[1]).Text = text;
        }

        static string Since(double? t)
        {
            if (t == null || t == 0) return "";
            double s = Math.Max(0, (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds - t.Value);
            return s < 60 ? "just now" : s < 3600 ? Fmt.N(s / 60) + " min ago" : Fmt.N(s / 3600, 1) + " h ago";
        }
    }
}

