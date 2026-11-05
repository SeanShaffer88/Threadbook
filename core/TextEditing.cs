using System.Text;

namespace Threadbook.Core;

public sealed record TextMatch(int Start, int Length);
public sealed record TextEdit(string Text, int SelectionStart, int SelectionLength, int ReplacementCount);

/// <summary>Pure literal operations; positions and limits are UTF-16 code units.</summary>
public static class TextEditing
{
    public static IReadOnlyList<TextMatch> Find(string text, string search, bool caseSensitive = false)
    {
        Check(text); ArgumentNullException.ThrowIfNull(search);
        var matches = new List<TextMatch>();
        if (search.Length == 0) return matches.AsReadOnly();
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (int position = 0; position <= text.Length - search.Length;)
        {
            int found = text.IndexOf(search, position, comparison);
            if (found < 0) break;
            matches.Add(new(found, search.Length)); position = found + search.Length;
        }
        return matches.AsReadOnly();
    }

    /// <summary>Inclusive boundary; pass selection end for Next, selection start minus one for Previous.</summary>
    public static TextMatch? Navigate(string text, string search, int boundary, bool previous = false, bool caseSensitive = false)
    {
        var matches = Find(text, search, caseSensitive);
        if (matches.Count == 0) return null;
        return previous ? matches.LastOrDefault(m => m.Start <= boundary) ?? matches[^1]
            : matches.FirstOrDefault(m => m.Start >= boundary) ?? matches[0];
    }

    public static TextEdit ReplaceCurrent(string text, string search, string replacement, int selectionStart, int selectionLength, bool caseSensitive = false)
    {
        Check(text); ArgumentNullException.ThrowIfNull(search); ArgumentNullException.ThrowIfNull(replacement);
        if (selectionStart < 0 || selectionLength < 0 || selectionStart > text.Length - selectionLength) throw new ArgumentOutOfRangeException(nameof(selectionStart));
        if (search.Length == 0 || selectionLength != search.Length || !text.AsSpan(selectionStart, selectionLength).Equals(search.AsSpan(), caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            return new(text, selectionStart, selectionLength, 0);
        if ((long)text.Length - selectionLength + replacement.Length > Markdown.MaxSourceLength)
            throw new DataException(ErrorCode.CapacityExceeded, "Edited body exceeds 100,000 characters.");
        var result = text.Remove(selectionStart, selectionLength).Insert(selectionStart, replacement);
        return new(result, selectionStart, replacement.Length, 1);
    }

    public static TextEdit ReplaceAll(string text, string search, string replacement, bool caseSensitive = false)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var matches = Find(text, search, caseSensitive);
        long length = text.Length + (long)matches.Count * (replacement.Length - search.Length);
        if (length > Markdown.MaxSourceLength) throw new DataException(ErrorCode.CapacityExceeded, "Edited body exceeds 100,000 characters.");
        var result = new StringBuilder((int)length); int cursor = 0;
        foreach (var match in matches) { result.Append(text, cursor, match.Start - cursor).Append(replacement); cursor = match.Start + match.Length; }
        result.Append(text, cursor, text.Length - cursor);
        return new(result.ToString(), matches.Count == 0 ? 0 : matches[0].Start, matches.Count == 0 ? 0 : replacement.Length, matches.Count);
    }

    private static void Check(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > Markdown.MaxSourceLength) throw new DataException(ErrorCode.CapacityExceeded, "Body exceeds 100,000 characters.");
    }
}
