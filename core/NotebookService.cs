namespace Threadbook.Core;

public sealed class NotebookService
{
    private readonly ISnapshotStore store;
    private readonly Func<DateTimeOffset> clock;
    private readonly object gate = new();
    public Snapshot State { get; private set; }
    public NotebookService(ISnapshotStore store, Func<DateTimeOffset>? clock = null)
    {
        this.store = store; this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        var initial = store.Load(); Validation.Check(initial); State = initial.Freeze();
    }
    public CommandPreview Preview(Command command, long expectedCanonicalRevision)
    {
        lock (gate)
        {
            try
            {
                if (State.Revision != expectedCanonicalRevision) return new(Outcome.Fail(ErrorCode.Conflict, "Canonical revision changed."), State, null);
                var books = State.Notebooks.ToList(); var notes = State.Notes.ToList(); var replies = State.Replies.ToList();
                var now = clock().ToUniversalTime();
                T Find<T>(IEnumerable<T> items, Func<T, bool> predicate) => items.FirstOrDefault(predicate) ?? throw new DataException(ErrorCode.NotFound, "This item no longer exists.");
                switch (command)
                {
                    case PutNotebook c:
                        var oldBook = c.Id is Guid bid ? Find(books, x => x.Id == bid) : null;
                        if (oldBook is not null) books.Remove(oldBook);
                        books.Add(new(c.Id ?? Guid.NewGuid(), c.Name.Trim(), oldBook?.Created ?? now)); break;
                    case DeleteNotebook c:
                        books.Remove(Find(books, x => x.Id == c.Id));
                        var removed = notes.Where(n => n.NotebookId == c.Id).Select(n => n.Id).ToHashSet();
                        notes.RemoveAll(n => removed.Contains(n.Id)); replies.RemoveAll(r => removed.Contains(r.NoteId)); break;
                    case PutNote c:
                        var oldNote = c.Id is Guid nid ? Find(notes, x => x.Id == nid) : null;
                        if (oldNote is not null) notes.Remove(oldNote);
                        notes.Add(new(c.Id ?? Guid.NewGuid(), c.NotebookId, c.Title.Trim(), c.Body, c.Flair.Trim(), oldNote?.Saved ?? false, oldNote?.Created ?? now, now)); break;
                    case DeleteNote c:
                        notes.Remove(Find(notes, x => x.Id == c.Id)); replies.RemoveAll(r => r.NoteId == c.Id); break;
                    case ToggleSaved c:
                        var saved = Find(notes, x => x.Id == c.Id); notes[notes.IndexOf(saved)] = saved with { Saved = !saved.Saved }; break;
                    case PutReply c:
                        var oldReply = c.Id is Guid rid ? Find(replies, x => x.Id == rid) : null;
                        if (oldReply is not null && (oldReply.NoteId != c.NoteId || oldReply.ParentId != c.ParentId)) throw new DataException(ErrorCode.Validation, "Editing a reply cannot reparent it.");
                        if (oldReply is not null) replies.Remove(oldReply);
                        replies.Add(new(c.Id ?? Guid.NewGuid(), c.NoteId, c.ParentId, c.Body.Trim(), oldReply?.Created ?? now, now));
                        var owner = Find(notes, x => x.Id == c.NoteId); notes[notes.IndexOf(owner)] = owner with { Updated = now }; break;
                    case DeleteReply c:
                        Find(replies, x => x.Id == c.Id); var subtree = Descendants(c.Id); replies.RemoveAll(r => subtree.Contains(r.Id)); break;
                    case ReplaceAll c:
                        Validation.Check(c.Data); books = c.Data.Notebooks.ToList(); notes = c.Data.Notes.ToList(); replies = c.Data.Replies.ToList(); break;
                    default: throw new DataException(ErrorCode.Validation, "Unknown command.");
                }
                var candidate = new Snapshot(1, checked(State.Revision + 1), books.AsReadOnly(), notes.AsReadOnly(), replies.AsReadOnly());
                Validation.Check(candidate);
                return new(Outcome.Ok(), State, candidate.Freeze());
            }
            catch (DataException ex) { return new(Outcome.Fail(ex.Code, ex.Message), State, null); }
            catch (OverflowException) { return new(Outcome.Fail(ErrorCode.CapacityExceeded, "Revision capacity exceeded."), State, null); }
        }
    }
    public Outcome Execute(Command command)
    {
        lock (gate) return CommitPreview(Preview(command, State.Revision));
    }
    public Outcome CommitPreview(CommandPreview preview)
    {
        lock (gate)
        {
            if (!preview.Outcome.Success) return preview.Outcome;
            if (preview.Candidate is null || State != preview.Before) return Outcome.Fail(ErrorCode.Conflict, "Preview is stale.");
            try
            {
                Validation.Check(preview.Candidate);
                var candidate = preview.Candidate.Freeze();
                Validation.Check(candidate);
                if (candidate.Revision != checked(State.Revision + 1)) return Outcome.Fail(ErrorCode.Conflict, "Preview revision is invalid.");
                var result = store.Commit(candidate, State.Revision);
                if (result.Success) State = candidate;
                return result;
            }
            catch (DataException ex) { return Outcome.Fail(ex.Code, ex.Message); }
            catch (OverflowException) { return Outcome.Fail(ErrorCode.CapacityExceeded, "Revision capacity exceeded."); }
        }
    }
    public void Reload() { lock (gate) { var loaded = store.Load(); Validation.Check(loaded); State = loaded.Freeze(); } }    public HashSet<Guid> Descendants(Guid replyId)
    {
        var children = State.Replies.ToLookup(r => r.ParentId);
        var result = new HashSet<Guid>(); var pending = new Queue<Guid>(); pending.Enqueue(replyId);
        while (pending.TryDequeue(out var id)) if (result.Add(id)) foreach (var r in children[id]) pending.Enqueue(r.Id);
        return result;
    }
    public IReadOnlyList<Note> Query(NoteQuery q)
    {
        var state = State; var search = q.Search.Trim();
        bool Match(string value) => value.Contains(search, StringComparison.OrdinalIgnoreCase);
        var matches = search.Length == 0 ? new HashSet<Guid>() : state.Replies.Where(r => Match(r.Body)).Select(r => r.NoteId).ToHashSet();
        var items = state.Notes.Where(n => (q.NotebookId is null || n.NotebookId == q.NotebookId) && (!q.SavedOnly || n.Saved) && (search.Length == 0 || Match(n.Title) || Match(n.Body) || Match(n.Flair) || matches.Contains(n.Id)));
        var sorted = q.Sort switch { NoteSort.Title => items.OrderBy(n => n.Title, StringComparer.OrdinalIgnoreCase), NoteSort.Created => items.OrderByDescending(n => n.Created), _ => items.OrderByDescending(n => n.Updated) };
        return Array.AsReadOnly(sorted.ThenBy(n => n.Id).ToArray());
    }
    public IReadOnlyList<Reply> Children(Guid noteId, Guid? parent = null) => Array.AsReadOnly(State.Replies.Where(r => r.NoteId == noteId && r.ParentId == parent).OrderBy(r => r.Created).ThenBy(r => r.Id).ToArray());
}
