using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace StrataHome
{
    /// <summary>
    /// A port of the web app's small Markdown renderer (serve/web/app.js: inline, blocks, markdown) to a WPF FlowDocument:
    /// paragraphs, h1-h6 (as h3/h4), lists, quotes, tables, rules, fenced code, `code`, **bold**, *italic*, [links](https://...).
    /// </summary>
    internal static class Markdown
    {
        static readonly Regex InlineRx = new Regex(
            @"`([^`\n]+)`|\*\*([^*\n]+)\*\*|\[([^\]\n]+)\]\((https?://[^)\s]+)\)|(?<![*\w])\*([^*\n]+)\*(?![*\w])", RegexOptions.Compiled);
        static readonly Regex OpenFence = new Regex(@"(^|\n)```([^\n`]*)\n", RegexOptions.Compiled);
        static readonly Regex CloseFence = new Regex(@"(^|\n)```[ \t]*(\n|$)", RegexOptions.Compiled);
        static readonly Regex Heading = new Regex(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Rule = new Regex(@"^\s*([-*_])\s*\1\s*\1[\s\1]*$", RegexOptions.Compiled);
        static readonly Regex Quote = new Regex(@"^>\s?(.*)$", RegexOptions.Compiled);
        static readonly Regex TableRow = new Regex(@"^\s*\|.*\|\s*$", RegexOptions.Compiled);
        static readonly Regex TableSep = new Regex(@"^\s*\|?[\s:-]+\|[\s|:-]*$", RegexOptions.Compiled);
        static readonly Regex ListItemRx = new Regex(@"^\s*(?:[-*+]|(\d+)[.)])\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Continuation = new Regex(@"^\s{2,}\S", RegexOptions.Compiled);

        public static FlowDocument Build(string text)
        {
            FlowDocument doc = new FlowDocument();
            doc.PagePadding = new Thickness(0);
            doc.FontFamily = Fonts.Regular;
            doc.FontSize = 16;
            doc.LineHeight = 24.8;
            doc.SetResourceReference(FlowDocument.ForegroundProperty, "StInkSoft");
            string rest = (text ?? "").Replace("\r\n", "\n");
            for (;;)
            {
                Match m = OpenFence.Match(rest);
                if (!m.Success) { Blocks(doc, rest); break; }
                Blocks(doc, rest.Substring(0, m.Index));
                rest = rest.Substring(m.Index + m.Length);
                string lang = m.Groups[2].Value.Trim();
                Match e = CloseFence.Match(rest);
                if (!e.Success) { CodeBlock(doc, lang, rest); break; }          // still streaming
                CodeBlock(doc, lang, rest.Substring(0, e.Index));
                rest = rest.Substring(e.Index + e.Length);
            }
            return doc;
        }

        // ------------------------------------------------------------------ inline

        static void Inline(InlineCollection into, string s, FontWeight weight, FontStyle style)
        {
            int pos = 0;
            foreach (Match m in InlineRx.Matches(s))
            {
                if (m.Index > pos) into.Add(Plain(s.Substring(pos, m.Index - pos), weight, style));
                if (m.Groups[1].Success)
                {
                    Run r = new Run(m.Groups[1].Value);
                    r.FontFamily = Fonts.Mono;
                    r.FontSize = 14.4;
                    r.SetResourceReference(TextElement.BackgroundProperty, "StSurface2");
                    r.FontWeight = weight;
                    into.Add(r);
                }
                else if (m.Groups[2].Success)
                {
                    Span b = new Span();
                    Inline(b.Inlines, m.Groups[2].Value, FontWeights.Bold, style);
                    into.Add(b);
                }
                else if (m.Groups[3].Success)
                {
                    Hyperlink h = new Hyperlink(new Run(m.Groups[3].Value));
                    try { h.NavigateUri = new Uri(m.Groups[4].Value); } catch { }
                    h.SetResourceReference(TextElement.ForegroundProperty, "StAccentText");
                    h.RequestNavigate += delegate(object s2, System.Windows.Navigation.RequestNavigateEventArgs e)
                    {
                        try { Process.Start(e.Uri.AbsoluteUri); } catch { }
                        e.Handled = true;
                    };
                    h.Click += delegate { if (h.NavigateUri != null) { try { Process.Start(h.NavigateUri.AbsoluteUri); } catch { } } };
                    into.Add(h);
                }
                else if (m.Groups[5].Success)
                {
                    Span i = new Span();
                    Inline(i.Inlines, m.Groups[5].Value, weight, FontStyles.Italic);
                    into.Add(i);
                }
                pos = m.Index + m.Length;
            }
            if (pos < s.Length) into.Add(Plain(s.Substring(pos), weight, style));
        }

        static Run Plain(string s, FontWeight weight, FontStyle style)
        {
            Run r = new Run(s);
            if (weight != FontWeights.Normal) r.FontWeight = weight;
            if (style != FontStyles.Normal) r.FontStyle = style;
            return r;
        }

        // ------------------------------------------------------------------ blocks

        sealed class ListState
        {
            public bool Ordered;
            public List<string> Items = new List<string>();
        }

        static void Blocks(FlowDocument doc, string text)
        {
            string[] lines = text.Split('\n');
            List<string> para = new List<string>();
            ListState list = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                Match m;
                if (l.Trim().Length == 0) { FlushPara(doc, para); FlushList(doc, ref list); continue; }
                if ((m = Heading.Match(l)).Success)
                {
                    FlushPara(doc, para); FlushList(doc, ref list);
                    Paragraph h = new Paragraph();
                    bool big = m.Groups[1].Length <= 2;
                    h.FontSize = big ? 18 : 16;
                    h.FontWeight = FontWeights.Bold;
                    h.Margin = new Thickness(0, big ? 16 : 12, 0, 8);
                    h.SetResourceReference(TextElement.ForegroundProperty, "StInk");
                    Inline(h.Inlines, m.Groups[2].Value, FontWeights.Bold, FontStyles.Normal);
                    doc.Blocks.Add(h);
                    continue;
                }
                if (Rule.IsMatch(l))
                {
                    FlushPara(doc, para); FlushList(doc, ref list);
                    Border b = new Border { Height = 1, Margin = new Thickness(0, 16, 0, 16), BorderThickness = new Thickness(0, 1, 0, 0) };
                    b.SetResourceReference(Border.BorderBrushProperty, "StLine");
                    doc.Blocks.Add(new BlockUIContainer(b));
                    continue;
                }
                if ((m = Quote.Match(l)).Success)
                {
                    FlushPara(doc, para); FlushList(doc, ref list);
                    Paragraph q = new Paragraph();
                    q.BorderThickness = new Thickness(3, 0, 0, 0);
                    q.Padding = new Thickness(12, 0, 0, 0);
                    q.Margin = new Thickness(0, 0, 0, 12);
                    q.SetResourceReference(Block.BorderBrushProperty, "StLine");
                    q.SetResourceReference(TextElement.ForegroundProperty, "StInkMuted");
                    Inline(q.Inlines, m.Groups[1].Value, FontWeights.Normal, FontStyles.Normal);
                    doc.Blocks.Add(q);
                    continue;
                }
                if (TableRow.IsMatch(l) && i + 1 < lines.Length && TableSep.IsMatch(lines[i + 1]))
                {
                    FlushPara(doc, para); FlushList(doc, ref list);
                    List<string[]> rows = new List<string[]>();
                    rows.Add(Cells(l));
                    i += 2;
                    while (i < lines.Length && TableRow.IsMatch(lines[i])) rows.Add(Cells(lines[i++]));
                    i--;
                    doc.Blocks.Add(MakeTable(rows));
                    continue;
                }
                if ((m = ListItemRx.Match(l)).Success)
                {
                    FlushPara(doc, para);
                    bool ordered = m.Groups[1].Success;
                    if (list == null || list.Ordered != ordered) { FlushList(doc, ref list); list = new ListState(); list.Ordered = ordered; }
                    list.Items.Add(m.Groups[2].Value);
                    continue;
                }
                if (list != null && Continuation.IsMatch(l)) { list.Items[list.Items.Count - 1] += " " + l.Trim(); continue; }
                FlushList(doc, ref list);
                para.Add(l);
            }
            FlushPara(doc, para);
            FlushList(doc, ref list);
        }

        static string[] Cells(string row)
        {
            string t = row.Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|")) t = t.Substring(0, t.Length - 1);
            string[] parts = t.Split('|');
            for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
            return parts;
        }

        static void FlushPara(FlowDocument doc, List<string> para)
        {
            if (para.Count == 0) return;
            Paragraph p = new Paragraph();
            p.Margin = new Thickness(0, 0, 0, 12);
            for (int i = 0; i < para.Count; i++)
            {
                if (i > 0) p.Inlines.Add(new LineBreak());
                Inline(p.Inlines, para[i], FontWeights.Normal, FontStyles.Normal);
            }
            doc.Blocks.Add(p);
            para.Clear();
        }

        static void FlushList(FlowDocument doc, ref ListState list)
        {
            if (list == null) return;
            List l = new List();
            l.MarkerStyle = list.Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;
            l.Margin = new Thickness(0, 0, 0, 12);
            l.Padding = new Thickness(24, 0, 0, 0);
            foreach (string item in list.Items)
            {
                Paragraph p = new Paragraph();
                p.Margin = new Thickness(0, 0, 0, 4);
                Inline(p.Inlines, item, FontWeights.Normal, FontStyles.Normal);
                l.ListItems.Add(new ListItem(p));
            }
            doc.Blocks.Add(l);
            list = null;
        }

        static Table MakeTable(List<string[]> rows)
        {
            Table t = new Table();
            t.CellSpacing = 0;
            t.Margin = new Thickness(0, 0, 0, 12);
            int cols = 0;
            foreach (string[] r in rows) cols = Math.Max(cols, r.Length);
            for (int c = 0; c < cols; c++) t.Columns.Add(new TableColumn());
            TableRowGroup g = new TableRowGroup();
            for (int ri = 0; ri < rows.Count; ri++)
            {
                TableRow tr = new TableRow();
                for (int c = 0; c < cols; c++)
                {
                    Paragraph p = new Paragraph();
                    p.Margin = new Thickness(0);
                    p.FontSize = 14;
                    Inline(p.Inlines, c < rows[ri].Length ? rows[ri][c] : "", ri == 0 ? FontWeights.Bold : FontWeights.Normal, FontStyles.Normal);
                    TableCell cell = new TableCell(p);
                    cell.BorderThickness = new Thickness(1);
                    cell.Padding = new Thickness(10, 6, 10, 6);
                    cell.SetResourceReference(Block.BorderBrushProperty, "StLine");
                    tr.Cells.Add(cell);
                }
                g.Rows.Add(tr);
            }
            t.RowGroups.Add(g);
            return t;
        }

        // ------------------------------------------------------------------ code

        static void CodeBlock(FlowDocument doc, string lang, string code)
        {
            Border box = new Border();
            box.CornerRadius = new CornerRadius(14);
            box.Margin = new Thickness(0, 12, 0, 12);
            box.SetResourceReference(Border.BackgroundProperty, "StCodeBg");
            Grid g = new Grid();
            g.RowDefinitions.Add(Ui.Row(Ui.Auto));
            g.RowDefinitions.Add(Ui.Row(Ui.Auto));

            Grid head = new Grid();
            head.Margin = new Thickness(14, 4, 4, 4);
            head.ColumnDefinitions.Add(Ui.Col(Ui.Star(1)));
            head.ColumnDefinitions.Add(Ui.Col(Ui.Auto));
            TextBlock lab = new TextBlock();
            lab.Text = lang.Length > 0 ? lang : "code";
            lab.FontFamily = Fonts.Medium;
            lab.FontSize = 12;
            lab.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xAB));
            lab.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(Ui.At(lab, 0, 0));
            Button copy = new Button();
            copy.Style = Ui.Style("StBtnIconOnCode");
            copy.Content = new IconView("copy", 16);
            copy.ToolTip = "Copy code";
            string captured = code;
            copy.Click += delegate { Ui.Copy(captured); };
            head.Children.Add(Ui.At(copy, 0, 1));
            Border sep = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Color.FromArgb(0x10, 255, 255, 255)) };
            g.Children.Add(Ui.At(head, 0, 0));
            g.Children.Add(Ui.At(sep, 0, 0));

            TextBox body = new TextBox();
            body.Text = code;
            body.IsReadOnly = true;
            body.BorderThickness = new Thickness(0);
            body.Background = Brushes.Transparent;
            body.FontFamily = Fonts.Mono;
            body.FontSize = 13;
            body.Padding = new Thickness(14);
            body.TextWrapping = TextWrapping.NoWrap;
            body.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            body.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            body.SetResourceReference(Control.ForegroundProperty, "StCodeInk");
            body.FocusVisualStyle = null;
            body.SetValue(TextBlock.LineHeightProperty, 20.8);
            body.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
            g.Children.Add(Ui.At(body, 1, 0));
            box.Child = g;
            doc.Blocks.Add(new BlockUIContainer(box));
        }
    }
}
