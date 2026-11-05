using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Threadbook.Core;
namespace Threadbook.Desktop;
internal static class MarkdownView
{
    public static FlowDocument Document(string source, int limit = 400, Action<WikiLinkToken>? onWikiLink = null, Func<WikiLinkToken, string?>? wikiState = null)
    {
        var parsed = Markdown.Parse(source);
        var doc = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 15, PagePadding = new Thickness(4), Background = Brushes.Transparent };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "Text");
        int inlineBudget = 2999, cellBudget = 1000; bool blockLimited = false;
        void AddBlock(Block block) { if (doc.Blocks.Count < 399) doc.Blocks.Add(block); else blockLimited = true; }
        void Notice(string text) { if (inlineBudget == 0) return; var p = new Paragraph(); AddRun(p.Inlines, text, ref inlineBudget); AddBlock(p); }
        limit = Math.Clamp(limit, 0, 400);
        foreach (var b in parsed.Blocks.Take(limit))
        {
            if (b.Kind == MarkdownBlockKind.Rule) { var p = new Paragraph(); AddRun(p.Inlines, "────────────────", ref inlineBudget); AddBlock(p); continue; }
            if (b.Kind == MarkdownBlockKind.Table)
            {
                var table = new Table { CellSpacing = 6 }; var group = new TableRowGroup(); table.RowGroups.Add(group);
                int rows = Math.Min(20, b.Cells!.Count / b.Columns), columns = Math.Min(10,b.Columns), rendered=0;
                for (int i = 0; i < rows && cellBudget >= columns; i++) { var row = new TableRow(); for (int j = 0; j < columns; j++) { var p = new Paragraph(); Inline(p.Inlines, b.Cells[i*b.Columns+j], ref inlineBudget, onWikiLink, wikiState); if (i == 0) p.FontWeight = FontWeights.Bold; row.Cells.Add(new TableCell(p)); cellBudget--; rendered++; } group.Rows.Add(row); }
                AddBlock(table); if(rendered < b.Cells.Count) Notice("Table preview limited to 20 rows, 10 columns and the document cell budget. Full table remains in Source/export."); continue;
            }
            var paragraph = new Paragraph { Margin = new Thickness(0, 3, 0, 9) };
            if (b.Kind == MarkdownBlockKind.CodeFence) { paragraph.FontFamily = new FontFamily("Consolas"); AddRun(paragraph.Inlines, b.Text, ref inlineBudget); }
            else
            {
                string prefix = b.Kind switch { MarkdownBlockKind.UnorderedItem => "•  ", MarkdownBlockKind.OrderedItem => b.Level + ".  ", MarkdownBlockKind.TaskItem => b.Checked ? "☑  " : "☐  ", MarkdownBlockKind.Quote => "│  ", _ => "" };
                if (prefix.Length > 0) AddRun(paragraph.Inlines, prefix, ref inlineBudget);
                Inline(paragraph.Inlines, b.Inlines, ref inlineBudget, onWikiLink, wikiState);
            }
            if (b.Kind == MarkdownBlockKind.Heading) { paragraph.FontSize = Math.Max(16, 30 - b.Level * 2); paragraph.FontWeight = FontWeights.SemiBold; }
            AddBlock(paragraph);
        }
        foreach (var d in parsed.Diagnostics) Notice(d.Message);
        if (inlineBudget == 0 || blockLimited || parsed.Blocks.Count > limit)
            doc.Blocks.Add(new Paragraph(new Run("Preview budget reached (400 blocks / 3,000 spans). Full content remains in Source/export.")));
        return doc;
    }
    private static void AddRun(InlineCollection destination, string text, ref int budget) { if (budget <= 0) return; budget--; destination.Add(new Run(text)); }
    private static void Inline(InlineCollection destination, IReadOnlyList<MarkdownInline> parts, ref int budget, Action<WikiLinkToken>? onWikiLink, Func<WikiLinkToken,string?>? wikiState)
    {
        foreach (var part in parts)
        {
            if (budget <= 0) break;
            if (part.Kind == MarkdownInlineKind.WikiLink && part.Wiki is { } token)
            {
                // Both the Hyperlink and its displayed Run consume the common budget.
                if (budget < 2) { budget = 0; break; }
                budget--;
                var link = new Hyperlink(); link.SetResourceReference(TextElement.ForegroundProperty, "Text");
                var state = wikiState?.Invoke(token);
                AddRun(link.Inlines, token.DisplayText + (state is null ? "" : " [" + state + "]"), ref budget);
                AutomationProperties.SetName(link, token.DisplayText + " · " + (state ?? "Local note link"));
                link.ToolTip = state ?? "Open local note";
                if (onWikiLink is not null) link.Click += (_, e) => { e.Handled = true; onWikiLink(token); };
                else link.IsEnabled = false;
                destination.Add(link); continue;
            }
            if (part.Children is { Count: > 0 })
            {
                budget--; var span = new Span(); Style(span, part.Kind);
                Inline(span.Inlines, part.Children, ref budget, onWikiLink, wikiState); destination.Add(span); continue;
            }
            budget--;
            var run = new Run(part.Kind == MarkdownInlineKind.Image ? "[Image: " + part.Text + "]" : part.Kind == MarkdownInlineKind.Link ? part.Text + " (" + part.Destination + ")" : part.Text);
            Style(run, part.Kind); destination.Add(run);
        }
    }
    private static void Style(TextElement element, MarkdownInlineKind kind)
    {
        if (kind == MarkdownInlineKind.Bold) element.FontWeight = FontWeights.Bold;
        if (kind == MarkdownInlineKind.Italic) element.FontStyle = FontStyles.Italic;
        if (kind == MarkdownInlineKind.Strike && element is System.Windows.Documents.Inline inline) inline.TextDecorations = TextDecorations.Strikethrough;
        if (kind == MarkdownInlineKind.Code) element.FontFamily = new FontFamily("Consolas");
    }
    public static RichTextBox Viewer(string source, Action<WikiLinkToken>? onWikiLink = null, Func<WikiLinkToken,string?>? wikiState = null) => new() { Document = Document(source, onWikiLink: onWikiLink, wikiState: wikiState), IsDocumentEnabled = true, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
}
