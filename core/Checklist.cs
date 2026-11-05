using System.Text.RegularExpressions;
namespace Threadbook.Core;
public static class Checklist
{
    // Deliberately supports ordinary Markdown list tasks only, never quoted/escaped literals.
    private static readonly Regex Task = new(@"^ {0,3}(?:[-+*]|[0-9]{1,9}[.)])[ \t]+\[([ xX])\](?:[ \t]|$)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static string Toggle(string body, int lineIndex, string expectedLine)
    {
        LibraryLogic.Require(body is not null && body.Length <= 100000 && expectedLine is not null && lineIndex >= 0, "Invalid checklist request.");
        var position = 0; var line = 0; char fence = '\0'; var fenceLength = 0;
        while (position <= body!.Length)
        {
            var end = position;
            while (end < body.Length && body[end] != '\r' && body[end] != '\n') end++;
            var text = body.Substring(position, end - position);
            var indent = 0; while (indent < text.Length && text[indent] == ' ') indent++;
            var marker = indent < text.Length ? text[indent] : '\0'; var run = 0;
            if (indent <= 3 && marker is '`' or '~') while (indent + run < text.Length && text[indent + run] == marker) run++;
            var wasFenced = fence != '\0'; var isFence = false;
            if (run >= 3)
            {
                var tail = text[(indent + run)..];
                if (fence == '\0' && (marker != '`' || !tail.Contains('`'))) { fence = marker; fenceLength = run; isFence = true; }
                else if (fence == marker && run >= fenceLength && string.IsNullOrWhiteSpace(tail)) { fence = '\0'; isFence = true; }
            }
            if (line == lineIndex)
            {
                if (!string.Equals(text, expectedLine, StringComparison.Ordinal)) throw new DataException(ErrorCode.Conflict, "The checklist line changed; refresh before toggling.");
                LibraryLogic.Require(!wasFenced && !isFence, "Checklist markers inside code fences cannot be toggled.");
                var match = Task.Match(text); LibraryLogic.Require(match.Success, "This line is not an editable checklist task.");
                var offset = position + match.Groups[1].Index;
                return body[..offset] + (body[offset] == ' ' ? 'x' : ' ') + body[(offset + 1)..];
            }
            if (end == body.Length) break;
            position = end + (body[end] == '\r' && end + 1 < body.Length && body[end + 1] == '\n' ? 2 : 1); line++;
        }
        throw new DataException(ErrorCode.Conflict, "The checklist line no longer exists.");
    }
}
