namespace Threadbook.Core;

public sealed record Notebook(Guid Id, string Name, DateTimeOffset Created);
public sealed record Note(Guid Id, Guid NotebookId, string Title, string Body, string Flair, bool Saved, DateTimeOffset Created, DateTimeOffset Updated);
public sealed record Reply(Guid Id, Guid NoteId, Guid? ParentId, string Body, DateTimeOffset Created, DateTimeOffset Updated);
public sealed record Snapshot(int SchemaVersion, long Revision, IReadOnlyList<Notebook> Notebooks, IReadOnlyList<Note> Notes, IReadOnlyList<Reply> Replies)
{
    public static Snapshot Empty => new(1, 0, Array.Empty<Notebook>(), Array.Empty<Note>(), Array.Empty<Reply>());
    public Snapshot Freeze() => this with { Notebooks = Array.AsReadOnly(Notebooks.ToArray()), Notes = Array.AsReadOnly(Notes.ToArray()), Replies = Array.AsReadOnly(Replies.ToArray()) };
}
public enum ErrorCode { Validation, NotFound, Conflict, CapacityExceeded, UnsupportedVersion, CorruptData, StorageFailure }
public sealed record Failure(ErrorCode Code, string Message);
public sealed record Outcome(bool Success, Failure? Error = null, string? Warning = null)
{
    public static Outcome Ok(string? warning = null) => new(true, Warning: warning);
    public static Outcome Fail(ErrorCode code, string message) => new(false, new(code, message));
}
public sealed class DataException(ErrorCode code, string message) : Exception(message) { public ErrorCode Code { get; } = code; }
public interface ISnapshotStore { Snapshot Load(); Outcome Commit(Snapshot candidate, long expectedRevision); }
public abstract record Command;
public sealed record PutNotebook(Guid? Id, string Name) : Command;
public sealed record DeleteNotebook(Guid Id) : Command;
public sealed record PutNote(Guid? Id, Guid NotebookId, string Title, string Body, string Flair) : Command;
public sealed record DeleteNote(Guid Id) : Command;
public sealed record ToggleSaved(Guid Id) : Command;
public sealed record PutReply(Guid? Id, Guid NoteId, Guid? ParentId, string Body) : Command;
public sealed record DeleteReply(Guid Id) : Command;
public sealed record ReplaceAll(Snapshot Data) : Command;
public enum NoteSort { Updated, Created, Title }
public sealed record NoteQuery(Guid? NotebookId = null, bool SavedOnly = false, string Search = "", NoteSort Sort = NoteSort.Updated);

public static class Validation
{
    public static void Check(Snapshot s)
    {
        void Require(bool value, string message) { if (!value) throw new DataException(ErrorCode.Validation, message); }
        if (s.SchemaVersion != 1) throw new DataException(ErrorCode.UnsupportedVersion, "This file uses an unsupported schema version. It has not been changed.");
        Require(s.Revision >= 0 && s.Revision < long.MaxValue, "Invalid revision.");
        Require(s.Notebooks is not null && s.Notes is not null && s.Replies is not null, "Missing collections.");
        if (s.Notebooks!.Count > 200 || s.Notes!.Count > 10000 || s.Replies!.Count > 50000) throw new DataException(ErrorCode.CapacityExceeded, "Collection limits exceeded (200 notebooks, 10,000 notes, 50,000 replies).");
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var books = new HashSet<Guid>();
        bool Time(DateTimeOffset t) => t != default && t.Offset == TimeSpan.Zero;
        foreach (var b in s.Notebooks)
        {
            Require(b is not null && b.Id != Guid.Empty && ids.Add(b.Id), "Invalid or duplicate notebook ID.");
            Require(b!.Name is { Length: > 0 and <= 60 } && b.Name == b.Name.Trim() && names.Add(b.Name), "Notebook names must be unique, trimmed, and 1–60 characters.");
            Require(Time(b.Created), "Invalid notebook timestamp."); books.Add(b.Id);
        }
        var notes = new HashSet<Guid>();
        foreach (var n in s.Notes!)
        {
            Require(n is not null && n.Id != Guid.Empty && ids.Add(n.Id) && books.Contains(n.NotebookId), "Invalid note ID or notebook reference.");
            Require(n!.Title is { Length: > 0 and <= 200 } && !string.IsNullOrWhiteSpace(n.Title) && n.Body is { Length: <= 100000 } && n.Flair is { Length: <= 40 }, "Notes need a title (1–200), body (up to 100,000), and flair (up to 40 characters).");
            Require(Time(n.Created) && Time(n.Updated) && n.Updated >= n.Created, "Invalid note timestamps."); notes.Add(n.Id);
        }
        var replies = new Dictionary<Guid, Reply>();
        foreach (var r in s.Replies!)
        {
            Require(r is not null && r.Id != Guid.Empty && ids.Add(r.Id) && notes.Contains(r.NoteId), "Invalid reply ID or note reference.");
            Require(r!.Body is { Length: > 0 and <= 20000 } && !string.IsNullOrWhiteSpace(r.Body), "Reply body must be 1–20,000 characters.");
            Require(Time(r.Created) && Time(r.Updated) && r.Updated >= r.Created, "Invalid reply timestamps."); replies.Add(r.Id, r);
        }
        foreach (var r in s.Replies!)
        {
            var seen = new HashSet<Guid> { r.Id }; var cursor = r; var depth = 1;
            while (cursor.ParentId is Guid parent)
            {
                Require(replies.TryGetValue(parent, out var p) && p.NoteId == r.NoteId && seen.Add(parent), "Reply parent is missing, belongs to another note, or forms a cycle.");
                Require(++depth <= 16, "Replies may be nested at most 16 levels."); cursor = p!;
            }
        }
    }
}
