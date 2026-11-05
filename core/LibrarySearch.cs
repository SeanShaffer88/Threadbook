namespace Threadbook.Core;
public sealed record SearchRequest(string Text = "", Guid? NotebookId = null, IReadOnlyList<Guid>? TagIds = null, DateTimeOffset? UpdatedFromUtc = null, DateTimeOffset? UpdatedBeforeUtc = null, bool SavedOnly = false, bool HasAttachments = false, bool HasTasks = false);
public enum SearchSource { Title, Body, Flair, Reply, Attachment }
public sealed record SearchHit(Guid NoteId, SearchSource Source, Guid? ReplyId, string Snippet);
public sealed record SearchResult(IReadOnlyList<SearchHit> Hits, int TotalCount);
public static class LibrarySearch
{
    public static SearchResult Search(Snapshot canonical, LibraryState library, SearchRequest request, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested(); Validation.Check(canonical); LibraryLogic.Validate(library);
        LibraryLogic.Require(request is not null && request.Text is { Length: <= 100000 }, "Invalid search text.");
        var q = request!;
        LibraryLogic.Require(q.NotebookId != Guid.Empty && (q.TagIds is null || q.TagIds.Count <= 32 && q.TagIds.All(x => x != Guid.Empty) && q.TagIds.Distinct().Count() == q.TagIds.Count), "Invalid search IDs.");
        LibraryLogic.Require((q.UpdatedFromUtc is null || q.UpdatedFromUtc.Value.Offset == TimeSpan.Zero) && (q.UpdatedBeforeUtc is null || q.UpdatedBeforeUtc.Value.Offset == TimeSpan.Zero) && (q.UpdatedFromUtc is null || q.UpdatedBeforeUtc is null || q.UpdatedFromUtc < q.UpdatedBeforeUtc), "Invalid UTC search range.");
        var replies = canonical.Replies.ToLookup(x => x.NoteId); var attachments = library.Attachments.ToLookup(x => x.NoteId); var taskOwners = library.Tasks.Select(x => x.NoteId).ToHashSet();
        var hits = new List<SearchHit>();
        foreach (var note in canonical.Notes.OrderByDescending(x => x.Updated).ThenBy(x => x.Id))
        {
            cancellation.ThrowIfCancellationRequested();
            if (q.NotebookId is Guid book && note.NotebookId != book || q.SavedOnly && !note.Saved || q.UpdatedFromUtc is DateTimeOffset from && note.Updated < from || q.UpdatedBeforeUtc is DateTimeOffset before && note.Updated >= before || q.HasAttachments && !attachments.Contains(note.Id) || q.HasTasks && !taskOwners.Contains(note.Id)) continue;
            if (q.TagIds is { Count: > 0 } required && (!library.NoteTags.TryGetValue(note.Id, out var assigned) || !required.All(assigned.Contains))) continue;
            SearchHit? hit = null;
            void Match(string value, SearchSource source, Guid? replyId = null)
            {
                if (hit is not null) return;
                var index = value.IndexOf(q.Text, StringComparison.OrdinalIgnoreCase);
                if (index < 0) return;
                var start = Math.Max(0, index - 60); var length = Math.Min(200, value.Length - start);
                // Do not split a UTF-16 surrogate pair at snippet boundaries.
                if (start > 0 && char.IsLowSurrogate(value[start]) && char.IsHighSurrogate(value[start - 1])) start--;
                length = Math.Min(200, value.Length - start);
                if (length > 0 && start + length < value.Length && char.IsHighSurrogate(value[start + length - 1])) length--;
                hit = new(note.Id, source, replyId, value.Substring(start, length));
            }
            Match(note.Title, SearchSource.Title); Match(note.Body, SearchSource.Body); Match(note.Flair, SearchSource.Flair);
            foreach (var reply in replies[note.Id].OrderBy(x => x.Id)) { cancellation.ThrowIfCancellationRequested(); Match(reply.Body, SearchSource.Reply, reply.Id); }
            foreach (var attachment in attachments[note.Id].OrderBy(x => x.Id)) { cancellation.ThrowIfCancellationRequested(); Match(attachment.DisplayName, SearchSource.Attachment); }
            if (hit is not null) hits.Add(hit);
        }
        cancellation.ThrowIfCancellationRequested();
        return new(Array.AsReadOnly(hits.ToArray()), hits.Count);
    }
}
