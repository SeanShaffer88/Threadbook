using System.Text;

namespace Threadbook.Core;

/// <summary>One distinct {{variable}} in a prompt, in first-appearance order.</summary>
public sealed record PromptVariable(string Name, string? DefaultValue, int Occurrences);

/// <summary>The rendered prompt plus the variables that still have no value.</summary>
public sealed record PromptRender(string Text, IReadOnlyList<string> Missing)
{
    public bool Complete => Missing.Count == 0;
}

/// <summary>An unfinished /word at the caret, offered to the slash menu.</summary>
public sealed record SlashQuery(int Start, int Length, string Query);

/// <summary>One reusable prompt part the slash menu can insert.</summary>
public sealed record PromptBlock(string Command, string Title, string Snippet);

/// <summary>
/// Pure prompt-template operations; positions are UTF-16 code units.
/// A variable is {{name}} or {{name = default}}. Prefix the opening braces
/// with a backslash (\{{name}}) to keep them as literal text.
/// </summary>
public static class PromptTemplates
{
    public const int MaxNameLength = 60;

    public static IReadOnlyList<PromptVariable> Parse(string text)
    {
        Check(text);
        var order = new List<string>();
        var found = new Dictionary<string, (string Name, string? Default, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var occurrence in Scan(text))
        {
            if (found.TryGetValue(occurrence.Name, out var existing))
            {
                found[occurrence.Name] = (existing.Name, existing.Default ?? occurrence.Default, existing.Count + 1);
            }
            else
            {
                found[occurrence.Name] = (occurrence.Name, occurrence.Default, 1);
                order.Add(occurrence.Name);
            }
        }
        return order.Select(name => { var v = found[name]; return new PromptVariable(v.Name, v.Default, v.Count); }).ToArray();
    }

    public static PromptRender Render(string text, IReadOnlyDictionary<string, string> values)
    {
        Check(text); ArgumentNullException.ThrowIfNull(values);
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values) lookup[pair.Key.Trim()] = pair.Value;
        var missing = new List<string>(); var missingSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new StringBuilder(text.Length);
        int cursor = 0;
        foreach (var occurrence in Scan(text, includeEscaped: true))
        {
            result.Append(text, cursor, occurrence.Start - cursor);
            if (occurrence.Escaped)
            {
                // Drop the backslash, keep the braces as literal text.
                result.Append(text, occurrence.Start + 1, occurrence.Length - 1);
            }
            else if (lookup.TryGetValue(occurrence.Name, out var value) && !string.IsNullOrEmpty(value))
            {
                result.Append(value);
            }
            else if (!string.IsNullOrEmpty(occurrence.Default))
            {
                result.Append(occurrence.Default);
            }
            else
            {
                result.Append(text, occurrence.Start, occurrence.Length);
                if (missingSeen.Add(occurrence.Name)) missing.Add(occurrence.Name);
            }
            cursor = occurrence.Start + occurrence.Length;
            if (result.Length > Markdown.MaxSourceLength)
                throw new DataException(ErrorCode.CapacityExceeded, "Rendered prompt exceeds 100,000 characters.");
        }
        result.Append(text, cursor, text.Length - cursor);
        if (result.Length > Markdown.MaxSourceLength)
            throw new DataException(ErrorCode.CapacityExceeded, "Rendered prompt exceeds 100,000 characters.");
        return new(result.ToString(), missing.AsReadOnly());
    }

    /// <summary>Finds a slash query ending at the caret: a '/' at a word start, then name letters only.</summary>
    public static SlashQuery? DetectSlash(string text, int caret)
    {
        Check(text);
        if (caret < 0 || caret > text.Length) throw new ArgumentOutOfRangeException(nameof(caret));
        int start = caret;
        while (start > 0 && IsNameChar(text[start - 1])) start--;
        if (start == 0 || text[start - 1] != '/') return null;
        int slash = start - 1;
        if (slash > 0 && !char.IsWhiteSpace(text[slash - 1])) return null;
        if (caret - slash > 21) return null;
        return new(slash, caret - slash, text[start..caret]);
    }

    public static TextEdit ApplyBlock(string text, SlashQuery query, PromptBlock block)
    {
        Check(text); ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(block);
        if (query.Start < 0 || query.Length < 0 || query.Start > text.Length - query.Length)
            throw new ArgumentOutOfRangeException(nameof(query));
        if ((long)text.Length - query.Length + block.Snippet.Length > Markdown.MaxSourceLength)
            throw new DataException(ErrorCode.CapacityExceeded, "Edited body exceeds 100,000 characters.");
        var result = text.Remove(query.Start, query.Length).Insert(query.Start, block.Snippet);
        return new(result, query.Start + block.Snippet.Length, 0, 1);
    }

    public static IReadOnlyList<PromptBlock> Blocks { get; } = Array.AsReadOnly(new PromptBlock[]
    {
        new("role", "Role — who the AI should be", "You are a {{role = senior expert}}.\n"),
        new("task", "Task — what to do", "Your task is to {{task}}.\n"),
        new("context", "Context — background it needs", "Context:\n{{context}}\n"),
        new("input", "Input — the material to work on", "Input:\n{{input}}\n"),
        new("steps", "Steps — work through it in order", "Work step by step:\n1. {{first step}}\n2. {{second step}}\n3. {{third step}}\n"),
        new("examples", "Examples — show the pattern", "Examples:\n- Input: {{example input}}\n  Output: {{example output}}\n"),
        new("constraints", "Constraints — limits and things to avoid", "Constraints:\n- {{constraint}}\n- Do not {{avoid}}\n"),
        new("format", "Output format — the shape of the answer", "Output format:\n{{format = a clear Markdown answer}}\n"),
        new("tone", "Tone — how it should sound", "Tone: {{tone = plain and direct}}.\n"),
        new("variable", "Variable — a fill-in blank", "{{variable}}"),
        new("ask", "Ask first — clarify before starting", "If anything is unclear, ask me before you start.\n"),
    });

    public static IReadOnlyList<PromptBlock> FindBlocks(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0) return Blocks;
        return Blocks.Where(b => b.Command.Contains(query, StringComparison.OrdinalIgnoreCase)
            || b.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private sealed record Occurrence(int Start, int Length, string Name, string? Default, bool Escaped);

    private static IEnumerable<Occurrence> Scan(string text, bool includeEscaped = false)
    {
        for (int i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] != '{' || text[i + 1] != '{') continue;
            bool escaped = i > 0 && text[i - 1] == '\\';
            if (escaped && !includeEscaped) { i++; continue; }
            int close = text.IndexOf("}}", i + 2, StringComparison.Ordinal);
            if (close < 0) yield break;
            string inner = text[(i + 2)..close];
            int start = escaped ? i - 1 : i;
            int length = close + 2 - start;
            if (TryParseInner(inner, out var name, out var defaultValue))
                yield return new(start, length, name, defaultValue, escaped);
            i = close + 1;
        }
    }

    private static bool TryParseInner(string inner, out string name, out string? defaultValue)
    {
        name = ""; defaultValue = null;
        if (inner.Length == 0 || inner.IndexOfAny(new[] { '{', '}', '\n', '\r' }) >= 0) return false;
        int equals = inner.IndexOf('=');
        string rawName = equals < 0 ? inner : inner[..equals];
        name = rawName.Trim();
        if (name.Length == 0 || name.Length > MaxNameLength) return false;
        if (equals >= 0)
        {
            defaultValue = inner[(equals + 1)..].Trim();
            if (defaultValue.Length == 0) defaultValue = null;
        }
        return true;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_';

    private static void Check(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > Markdown.MaxSourceLength) throw new DataException(ErrorCode.CapacityExceeded, "Body exceeds 100,000 characters.");
    }
}
