using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Threadbook.Core;

public enum MarkdownBlockKind { Paragraph, Heading, UnorderedItem, OrderedItem, TaskItem, Quote, CodeFence, Rule, Table }
public enum MarkdownInlineKind { Text, Bold, Italic, Strike, Code, Link, Image, WikiLink }
public sealed record MarkdownInline(MarkdownInlineKind Kind, string Text, int SourceStart, int SourceLength, string? Destination = null, WikiLinkToken? Wiki = null, IReadOnlyList<MarkdownInline>? Children = null);
public sealed record MarkdownBlock(MarkdownBlockKind Kind, string Text, int SourceStart, int SourceLength,
    IReadOnlyList<MarkdownInline> Inlines, int Level = 0, bool Checked = false,
    IReadOnlyList<IReadOnlyList<MarkdownInline>>? Cells = null, int Columns = 0);
public sealed record MarkdownDiagnostic(int SourceStart, string Message);
public sealed record MarkdownDocument(IReadOnlyList<MarkdownBlock> Blocks, IReadOnlyList<MarkdownBlock> Headings,
    IReadOnlyList<MarkdownDiagnostic> Diagnostics, int SourceWordCount);

/// <summary>Bounded native subset, not CommonMark/GFM. No IO or URL activation. Offsets index original UTF-16 source.</summary>
public static class Markdown
{
    public const int MaxSourceLength = 100000;
    private sealed record Line(string Text, int Start, int Length);
    private static Match Match(string text, string pattern) => Regex.Match(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static int CountSourceWords(string source)
    {
        Check(source); int count = 0; bool inWord = false;
        foreach (char c in source) { bool word = !char.IsWhiteSpace(c); if (word && !inWord) count++; inWord = word; }
        return count;
    }
    private static void Check(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > MaxSourceLength) throw new DataException(ErrorCode.CapacityExceeded, "Markdown exceeds 100,000 characters.");
    }
    public static MarkdownDocument Parse(string source) => Parse(source, default);
    internal static MarkdownDocument Parse(string source, CancellationToken cancellationToken)
    {
        Check(source);
        var lines = new List<Line>();
        for (int p = 0; p < source.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = p; while (end < source.Length && source[end] != '\r' && source[end] != '\n') end++;
            int next = end; if (next < source.Length && source[next++] == '\r' && next < source.Length && source[next] == '\n') next++;
            lines.Add(new(source[p..end], p, next - p)); p = next;
        }
        var blocks = new List<MarkdownBlock>(); var diagnostics = new List<MarkdownDiagnostic>();
        int failedTableThrough = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[i]; string text = line.Text;
            if (string.IsNullOrWhiteSpace(text)) continue;
            MarkdownBlock Block(MarkdownBlockKind kind, string value, int offset, int level = 0, bool check = false) =>
                new(kind, value, line.Start, line.Text.Length, ParseInlineCore(value, line.Start + offset, 0, cancellationToken), level, check);
            var fence = Match(text, "^(`{3,}|~{3,})([^`~]*)$");
            if (fence.Success)
            {
                string marker = fence.Groups[1].Value; int close = i + 1;
                while (close < lines.Count && !IsFenceClose(lines[close].Text, marker)) close++;
                if (close < lines.Count)
                {
                    int contentStart = line.Start + line.Length;
                    string content = source[contentStart..lines[close].Start];
                    blocks.Add(new(MarkdownBlockKind.CodeFence, content, line.Start, lines[close].Start + lines[close].Text.Length - line.Start, Array.Empty<MarkdownInline>()));
                    i = close; continue;
                }
                diagnostics.Add(new(line.Start, "Unclosed fence displayed literally."));
                // Preserve the complete unclosed fence; headings inside it are not outline entries.
                string literal = source[line.Start..];
                blocks.Add(new(MarkdownBlockKind.Paragraph, literal, line.Start, literal.Length, Array.AsReadOnly(new[] { new MarkdownInline(MarkdownInlineKind.Text, literal, line.Start, literal.Length) })));
                break;
            }
            var heading = Match(text, "^(#{1,6})[ \\t]+(.+)$");
            if (heading.Success) { blocks.Add(Block(MarkdownBlockKind.Heading, heading.Groups[2].Value, heading.Groups[2].Index, heading.Groups[1].Length)); continue; }
            if (Match(text, "^ {0,3}((\\*[ \\t]*){3,}|(-[ \\t]*){3,}|(_[ \\t]*){3,})$").Success) { blocks.Add(Block(MarkdownBlockKind.Rule, "", 0)); continue; }
            if (i > failedTableThrough && i + 1 < lines.Count && TryTable(lines, i, ref failedTableThrough, cancellationToken, out var cells, out int columns, out int last))
            {
                blocks.Add(new(MarkdownBlockKind.Table, source[line.Start..(lines[last].Start + lines[last].Text.Length)], line.Start,
                    lines[last].Start + lines[last].Text.Length - line.Start, Array.Empty<MarkdownInline>(), Cells: cells, Columns: columns)); i = last; continue;
            }
            var item = Match(text, "^([-+*]|[0-9]{1,9}\\.)[ \\t]+(.*)$");
            if (item.Success)
            {
                var body = item.Groups[2]; var task = Match(body.Value, "^\\[([ xX])\\][ \\t]+(.*)$");
                if (task.Success) blocks.Add(Block(MarkdownBlockKind.TaskItem, task.Groups[2].Value, body.Index + task.Groups[2].Index, check: task.Groups[1].Value != " "));
                else blocks.Add(Block(char.IsDigit(text[0]) ? MarkdownBlockKind.OrderedItem : MarkdownBlockKind.UnorderedItem, body.Value, body.Index,
                    char.IsDigit(text[0]) ? int.Parse(item.Groups[1].Value.AsSpan(0, item.Groups[1].Length - 1), System.Globalization.CultureInfo.InvariantCulture) : 0));
                continue;
            }
            if (text.StartsWith("> ", StringComparison.Ordinal)) { blocks.Add(Block(MarkdownBlockKind.Quote, text[2..], 2)); continue; }
            blocks.Add(Block(MarkdownBlockKind.Paragraph, text, 0));
        }
        return new(blocks.AsReadOnly(), blocks.Where(b => b.Kind == MarkdownBlockKind.Heading).ToList().AsReadOnly(), diagnostics.AsReadOnly(), CountSourceWords(source));
    }
    private static bool IsFenceClose(string text, string marker) => text.TrimEnd().Length >= marker.Length && text.TrimEnd().All(c => c == marker[0]);

    private static bool TryTable(List<Line> lines, int index, ref int failedThrough, CancellationToken cancellationToken, out IReadOnlyList<IReadOnlyList<MarkdownInline>> cells, out int columns, out int last)
    {
        var result = new List<IReadOnlyList<MarkdownInline>>(); cells = result.AsReadOnly(); columns = 0; last = index;
        if (!lines[index].Text.Contains('|') || !lines[index + 1].Text.Contains('|') ||
            HasOutsideEscapedPipe(lines[index].Text) || HasOutsideEscapedPipe(lines[index + 1].Text)) return false;
        var header = SplitCells(lines[index]); var delimiter = SplitCells(lines[index + 1]);
        if (header.Count < 2 || header.Count != delimiter.Count || delimiter.Any(c => !Match(c.Text, "^:?-{3,}:?$").Success)) return false;
        columns = header.Count; var rows = new List<List<(string Text, int Start)>> { header }; int next = index + 2;
        while (next < lines.Count && lines[next].Text.Contains('|') && !string.IsNullOrWhiteSpace(lines[next].Text))
        {
            var row = SplitCells(lines[next]);
            if (row.Count != columns || HasOutsideEscapedPipe(lines[next].Text))
            {
                // All intervening rows have this width: none can start a table past this invalid body row.
                failedThrough = next - 1; return false;
            }
            rows.Add(row); next++;
        }
        foreach (var row in rows) foreach (var cell in row) result.Add(ParseInlineCore(cell.Text, cell.Start, 0, cancellationToken));
        last = next - 1; return true;
    }
    private static List<(string Text, int Start)> SplitCells(Line line)
    {
        int start = 0, end = line.Text.Length;
        while (start < end && char.IsWhiteSpace(line.Text[start])) start++;
        while (end > start && char.IsWhiteSpace(line.Text[end - 1])) end--;
        if (start < end && line.Text[start] == '|') start++;
        if (end > start && line.Text[end - 1] == '|') end--;
        var cells = new List<(string, int)>();
        for (int p = start; p <= end; p++)
        {
            if (p < end && WikiLinks.TryRead(line.Text, p, line.Start, out var wiki)) { p += wiki!.SourceLength - 1; continue; }
            if (p != end && line.Text[p] != '|') continue;
            int a = start, b = p; while (a < b && char.IsWhiteSpace(line.Text[a])) a++; while (b > a && char.IsWhiteSpace(line.Text[b - 1])) b--;
            cells.Add((line.Text[a..b], line.Start + a)); start = p + 1;
        }
        return cells;
    }
    private static bool HasOutsideEscapedPipe(string text)
    {
        for (int p=0;p<text.Length;p++)
        {
            if (WikiLinks.TryRead(text,p,0,out var wiki)) { p+=wiki!.SourceLength-1; continue; }
            if (text[p]=='\\' && p+1<text.Length && text[p+1]=='|') return true;
        }
        return false;
    }

    /// <summary>Single-level inline spans. Nested markup and unmatched delimiters remain literal.</summary>
    public static IReadOnlyList<MarkdownInline> ParseInline(string text, int sourceStart = 0)
        => ParseInlineCore(text, sourceStart, 0, default);
    private static IReadOnlyList<MarkdownInline> ParseInlineCore(string text, int sourceStart, int depth, CancellationToken cancellationToken)
    {
        Check(text); if (sourceStart < 0) throw new ArgumentOutOfRangeException(nameof(sourceStart));
        var result = new List<MarkdownInline>(); int plain = 0, p = 0;
        void Flush(int end) { if (end > plain) result.Add(new(MarkdownInlineKind.Text, text[plain..end], sourceStart + plain, end - plain)); }
        // Precompute next closing tokens: malformed delimiter runs cannot cause quadratic scans.
        var tokens = new[] { "**", "__", "~~", "`", "*", "_", "](", ")" };
        var protectedWiki = new bool[text.Length];
        for(int n=0;n<text.Length;n++)
        {
            if((n&255)==0) cancellationToken.ThrowIfCancellationRequested();
            if(WikiLinks.TryRead(text,n,sourceStart,out var protectedToken,cancellationToken))
            { Array.Fill(protectedWiki,true,n,protectedToken!.SourceLength); n+=protectedToken.SourceLength-1; }
        }
        var next = new Dictionary<string, int[]>();
        foreach (var token in tokens)
        {
            var offsets = new int[text.Length + 1]; int found = -1; offsets[text.Length] = -1;
            for (int n = text.Length - 1; n >= 0; n--) { if ((!protectedWiki[n] || token == "`") && text.AsSpan(n).StartsWith(token, StringComparison.Ordinal)) found = n; offsets[n] = found; }
            next[token] = offsets;
        }
        while (p < text.Length)
        {
            if ((p & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (text.AsSpan(p).StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                text.AsSpan(p).StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                text.AsSpan(p).StartsWith("www.", StringComparison.OrdinalIgnoreCase) || IsBareUriStart(text,p) ||
                (text[p] == '<' && p+1<text.Length && text.AsSpan(p+1).StartsWith("http", StringComparison.OrdinalIgnoreCase)))
            { while (p < text.Length && !char.IsWhiteSpace(text[p])) p++; continue; }
            if (WikiLinks.TryRead(text,p,sourceStart,out var wiki,cancellationToken))
            { Flush(p); result.Add(new(MarkdownInlineKind.WikiLink,wiki!.DisplayText,wiki.SourceStart,wiki.SourceLength,Wiki:wiki)); p+=wiki.SourceLength; plain=p; continue; }
            int start = p, finish = -1; string? value = null, destination = null; var kind = MarkdownInlineKind.Text;
            bool image = text[p] == '!' && p + 1 < text.Length && text[p + 1] == '[';
            if (image || text[p] == '[')
            {
                int labelStart = p + (image ? 2 : 1), middle = next["]("][Math.Min(labelStart, text.Length)];
                if (middle >= 0)
                {
                    int close = next[")"][middle + 2];
                    if (close >= 0) { value = text[labelStart..middle]; destination = text[(middle + 2)..close]; finish = close + 1; kind = image ? MarkdownInlineKind.Image : MarkdownInlineKind.Link; }
                }
            }
            else
            {
                string? delimiter = tokens.Take(6).FirstOrDefault(t => text.AsSpan(p).StartsWith(t, StringComparison.Ordinal));
                if (delimiter is not null && p + delimiter.Length <= text.Length)
                {
                    int close = next[delimiter][p + delimiter.Length];
                    if (close > p + delimiter.Length)
                    {
                        value = text[(p + delimiter.Length)..close]; finish = close + delimiter.Length;
                        kind = delimiter switch { "**" or "__" => MarkdownInlineKind.Bold, "~~" => MarkdownInlineKind.Strike, "`" => MarkdownInlineKind.Code, _ => MarkdownInlineKind.Italic };
                    }
                }
            }
            if (finish >= 0) {
                Flush(start);
                IReadOnlyList<MarkdownInline>? children = null;
                if (depth < 16 && kind is MarkdownInlineKind.Bold or MarkdownInlineKind.Italic or MarkdownInlineKind.Strike)
                {
                    int delimiterLength = kind == MarkdownInlineKind.Italic ? 1 : 2;
                    children = ParseInlineCore(value!,sourceStart+start+delimiterLength,depth+1,cancellationToken);
                    // Preserve the existing flat rendering of ordinary Markdown. Composite children
                    // are needed only where a local wiki link participates in the styled span.
                    if (!children.Any(c => c.Wiki is not null || c.Children is not null)) children = null;
                }
                result.Add(new(kind, value!, sourceStart + start, finish - start, destination, Children:children)); p = finish; plain = p;
            }
            else p++;
        }
        Flush(text.Length); return result.AsReadOnly();
    }
    private static bool IsBareUriStart(string text,int p)
    {
        if(!char.IsAsciiLetter(text[p]) || (p>0 && (char.IsAsciiLetterOrDigit(text[p-1]) || text[p-1] is '+' or '-' or '.'))) return false;
        int end=p+1;
        while(end<text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] is '+' or '-' or '.')) end++;
        return end<text.Length && text[end]==':';
    }

    /// <summary>Pure self-contained export. URLs are encoded visible text, never href/src attributes.</summary>
    public static string ExportHtml(string title, string source)
    {
        ArgumentNullException.ThrowIfNull(title); if (title.Length > 200) throw new DataException(ErrorCode.CapacityExceeded, "Title exceeds 200 characters.");
        var doc = Parse(source); string E(string s) => WebUtility.HtmlEncode(s);
        string Inline(IReadOnlyList<MarkdownInline> spans) => string.Concat(spans.Select(s => s.Kind switch
        {
            MarkdownInlineKind.Bold => "<strong>" + (s.Children is null ? E(s.Text) : Inline(s.Children)) + "</strong>", MarkdownInlineKind.Italic => "<em>" + (s.Children is null ? E(s.Text) : Inline(s.Children)) + "</em>",
            MarkdownInlineKind.Strike => "<del>" + (s.Children is null ? E(s.Text) : Inline(s.Children)) + "</del>", MarkdownInlineKind.Code => "<code>" + E(s.Text) + "</code>",
            MarkdownInlineKind.WikiLink => E(source.Substring(s.SourceStart,s.SourceLength)),
            MarkdownInlineKind.Link => E(s.Text) + " (" + E(s.Destination ?? "") + ")",
            MarkdownInlineKind.Image => "[image: " + E(s.Text) + "]", _ => E(s.Text)
        }));
        var html = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><title>");
        html.Append(E(title)).Append("</title><style>body{max-width:70ch;margin:3rem auto;padding:0 1rem;font:16px/1.6 system-ui;color:#242424;background:#fff}pre,code{background:#eee;white-space:pre-wrap;overflow-wrap:anywhere}blockquote{border-left:3px solid #888;padding-left:1rem}table{border-collapse:collapse}td,th{border:1px solid #888;padding:.4rem}p{white-space:pre-wrap}</style></head><body>");
        foreach (var b in doc.Blocks)
        {
            string content = Inline(b.Inlines);
            switch (b.Kind)
            {
                case MarkdownBlockKind.Heading: html.Append($"<h{b.Level}>").Append(content).Append($"</h{b.Level}>"); break;
                case MarkdownBlockKind.CodeFence: html.Append("<pre><code>").Append(E(b.Text)).Append("</code></pre>"); break;
                case MarkdownBlockKind.Rule: html.Append("<hr>"); break;
                case MarkdownBlockKind.Quote: html.Append("<blockquote>").Append(content).Append("</blockquote>"); break;
                case MarkdownBlockKind.OrderedItem: html.Append("<ol start=\"").Append(b.Level).Append("\"><li>").Append(content).Append("</li></ol>"); break;
                case MarkdownBlockKind.UnorderedItem: html.Append("<ul><li>").Append(content).Append("</li></ul>"); break;
                case MarkdownBlockKind.TaskItem: html.Append("<p>").Append(b.Checked ? "☑ " : "☐ ").Append(content).Append("</p>"); break;
                case MarkdownBlockKind.Table:
                    html.Append("<table>");
                    for (int c = 0; c < b.Cells!.Count; c++) { if (c % b.Columns == 0) html.Append("<tr>"); string tag = c < b.Columns ? "th" : "td"; html.Append('<').Append(tag).Append('>').Append(Inline(b.Cells[c])).Append("</").Append(tag).Append('>'); if (c % b.Columns == b.Columns - 1) html.Append("</tr>"); }
                    html.Append("</table>"); break;
                default: html.Append("<p>").Append(content).Append("</p>"); break;
            }
        }
        return html.Append("</body></html>").ToString();
    }
}
