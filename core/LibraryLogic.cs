namespace Threadbook.Core;

public static class LibraryLogic
{
    internal static void Require(bool condition, string message) { if (!condition) throw new DataException(ErrorCode.Validation, message); }
    internal static void Limit(bool condition, string message) { if (!condition) throw new DataException(ErrorCode.CapacityExceeded, message); }
    public static void Validate(LibraryState s)
    {
        Require(s is not null, "Missing library state.");
        if (s!.SchemaVersion != 1) throw new DataException(ErrorCode.UnsupportedVersion, "Unsupported library schema.");
        Require(s.Revision >= 0 && s.Revision < long.MaxValue, "Invalid library revision.");
        Require(s.Folders is not null && s.NotebookFolders is not null && s.Tags is not null && s.NoteTags is not null && s.Templates is not null && s.Tasks is not null && s.Attachments is not null, "Missing metadata collections.");
        Limit(s.Folders!.Count <= 1000 && s.Tags!.Count <= 2000 && s.Templates!.Count <= 200 && s.Tasks!.Count <= 20000 && s.Attachments!.Count <= 10000, "Metadata collection limit exceeded.");
        var ids = new HashSet<Guid>();
        void Id(Guid id) => Require(id != Guid.Empty && ids.Add(id), "Empty or duplicate metadata ID.");
        void Name(string name) => Require(name is { Length: > 0 and <= 60 } && name == name.Trim() && !string.IsNullOrWhiteSpace(name), "Names must be trimmed, nonblank, and at most 60 characters.");
        var folders = new Dictionary<Guid, LibraryFolder>();
        var folderNames = new HashSet<(Guid?, string)>();
        foreach (var f in s.Folders) { Require(f is not null, "Missing folder."); Id(f!.Id); Name(f.Name); Require(folderNames.Add((f.ParentId, f.Name.ToUpperInvariant())), "Duplicate sibling folder name."); folders.Add(f.Id, f); }
        foreach (var f in s.Folders)
        {
            var visited = new HashSet<Guid> { f.Id }; var current = f; var depth = 1;
            while (current.ParentId is Guid parent)
            {
                Require(folders.TryGetValue(parent, out var next) && visited.Add(parent), "Missing folder parent or cycle.");
                Limit(++depth <= 8, "Folder depth exceeds eight."); current = next!;
            }
        }
        foreach (var pair in s.NotebookFolders!) Require(pair.Key != Guid.Empty && folders.ContainsKey(pair.Value), "Invalid notebook folder assignment.");
        var tags = new HashSet<Guid>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in s.Tags!) { Require(t is not null, "Missing tag."); Id(t!.Id); Name(t.Name); Require(names.Add(t.Name), "Duplicate tag name."); tags.Add(t.Id); }
        void TagList(IReadOnlyList<Guid> values) { Require(values is not null, "Missing tag list."); Limit(values!.Count <= 32, "At most 32 tags per item."); Require(values.Distinct().Count() == values.Count && values.All(tags.Contains), "Duplicate or missing tag reference."); }
        foreach (var pair in s.NoteTags!) { Require(pair.Key != Guid.Empty, "Invalid note assignment."); TagList(pair.Value); }
        names.Clear();
        foreach (var t in s.Templates!)
        {
            Require(t is not null, "Missing template."); Id(t!.Id); Name(t.Name); Require(names.Add(t.Name), "Duplicate template name.");
            Require(t.Title is { Length: > 0 and <= 200 } && !string.IsNullOrWhiteSpace(t.Title) && t.Body is { Length: <= 100000 } && t.Flair is { Length: <= 40 }, "Invalid template text."); TagList(t.TagIds);
        }
        bool Time(DateTimeOffset? time) => time is null || (time != default(DateTimeOffset) && time.Value.Offset == TimeSpan.Zero);
        foreach (var t in s.Tasks!)
        {
            Require(t is not null, "Missing task."); Id(t!.Id); Require(t.NoteId != Guid.Empty && t.Title is { Length: > 0 and <= 200 } && !string.IsNullOrWhiteSpace(t.Title), "Invalid task.");
            Require(Time(t.DueUtc) && Time(t.SnoozedUntilUtc), "Task dates must be valid UTC timestamps.");
        }
        long total = 0;
        foreach (var a in s.Attachments!)
        {
            Require(a is not null, "Missing attachment."); Id(a!.Id);
            Require(a.NoteId != Guid.Empty && a.DisplayName is { Length: > 0 and <= 255 } && !string.IsNullOrWhiteSpace(a.DisplayName) && !a.DisplayName.Any(char.IsControl), "Invalid attachment name.");
            Require(a.MediaType is { Length: > 0 and <= 127 } && a.MediaType.Count(c => c == '/') == 1 && a.MediaType.Split('/').All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || "!#$&^_.+-".Contains(c))), "Invalid attachment media type.");
            Limit(a.Length >= 0 && a.Length <= 25L * 1024 * 1024, "Attachment exceeds 25 MiB.");
            Require(a.Sha256 is { Length: 64 } && a.Sha256.All(Uri.IsHexDigit), "Invalid SHA256.");
            total += a.Length; Limit(total <= 512L * 1024 * 1024, "Attachments exceed 512 MiB.");
        }
    }
    public static LibraryLoadResult Normalize(LibraryState state, Snapshot canonical)
    {
        Validate(state); Validation.Check(canonical);
        var books = canonical.Notebooks.Select(x => x.Id).ToHashSet(); var notes = canonical.Notes.Select(x => x.Id).ToHashSet();
        var projected = state with
        {
            NotebookFolders = state.NotebookFolders.Where(x => books.Contains(x.Key)).ToDictionary(),
            NoteTags = state.NoteTags.Where(x => notes.Contains(x.Key)).ToDictionary(),
            Tasks = state.Tasks.Where(x => notes.Contains(x.NoteId)).ToArray(),
            Attachments = state.Attachments.Where(x => notes.Contains(x.NoteId)).ToArray()
        };
        var stale = projected.NotebookFolders.Count != state.NotebookFolders.Count || projected.NoteTags.Count != state.NoteTags.Count || projected.Tasks.Count != state.Tasks.Count || projected.Attachments.Count != state.Attachments.Count;
        return new(projected.Freeze(), stale ? "Stale note or notebook references were omitted from the usable library; stored metadata was not rewritten." : null, false);
    }
}

public sealed class LibraryService
{
    private readonly ILibraryStore store;
    private readonly Func<Snapshot> canonical;
    private readonly object gate = new();
    public LibraryState State { get; private set; }
    public string? Warning { get; private set; }
    public bool WritesBlocked { get; }
    public LibraryService(ILibraryStore store, Func<Snapshot> canonical)
    {
        this.store = store; this.canonical = canonical;
        var snapshot = canonical(); var loaded = store.Load(snapshot);
        var normalized = LibraryLogic.Normalize(loaded.State, snapshot);
        State = normalized.State; Warning = loaded.Warning ?? normalized.Warning; WritesBlocked = loaded.WritesBlocked;
    }
    public Outcome Execute(LibraryCommand command)
    {
        lock (gate)
        {
            if (WritesBlocked) return Outcome.Fail(ErrorCode.CorruptData, "Library writes are blocked; preserve and explicitly recover the metadata first.");
            try
            {
                var snapshot = canonical(); var normalized = LibraryLogic.Normalize(State, snapshot);
                var s = normalized.State;
                var folders = s.Folders.ToList(); var assignments = s.NotebookFolders.ToDictionary(); var tags = s.Tags.ToList(); var noteTags = s.NoteTags.ToDictionary(); var templates = s.Templates.ToList(); var tasks = s.Tasks.ToList(); var attachments = s.Attachments.ToList();
                T Find<T>(IEnumerable<T> values, Func<T, bool> predicate) => values.FirstOrDefault(predicate) ?? throw new DataException(ErrorCode.NotFound, "The library item no longer exists.");
                void NoteExists(Guid id) => LibraryLogic.Require(snapshot.Notes.Any(x => x.Id == id), "The note no longer exists.");
                switch (command)
                {
                    case PutLibraryFolder c:
                        if (c.Id is Guid fid) folders.Remove(Find(folders, x => x.Id == fid));
                        folders.Add(new(c.Id ?? Guid.NewGuid(), c.ParentId, c.Name)); break;
                    case DeleteLibraryFolder c:
                        var folder = Find(folders, x => x.Id == c.Id); folders.Remove(folder);
                        folders = folders.Select(x => x.ParentId == c.Id ? x with { ParentId = folder.ParentId } : x).ToList();
                        foreach (var key in assignments.Where(x => x.Value == c.Id).Select(x => x.Key).ToArray())
                            if (folder.ParentId is Guid parent) assignments[key] = parent; else assignments.Remove(key);
                        break;
                    case AssignNotebookFolder c:
                        LibraryLogic.Require(snapshot.Notebooks.Any(x => x.Id == c.NotebookId), "The notebook no longer exists.");
                        if (c.FolderId is Guid target) assignments[c.NotebookId] = target; else assignments.Remove(c.NotebookId); break;
                    case PutLibraryTag c:
                        if (c.Id is Guid tid) tags.Remove(Find(tags, x => x.Id == tid));
                        tags.Add(new(c.Id ?? Guid.NewGuid(), c.Name)); break;
                    case DeleteLibraryTag c:
                        tags.Remove(Find(tags, x => x.Id == c.Id));
                        foreach (var key in noteTags.Keys.ToArray()) noteTags[key] = noteTags[key].Where(x => x != c.Id).ToArray();
                        templates = templates.Select(x => x with { TagIds = x.TagIds.Where(id => id != c.Id).ToArray() }).ToList(); break;
                    case AssignNoteTags c: NoteExists(c.NoteId); LibraryLogic.Require(c.TagIds is not null, "Missing tags."); noteTags[c.NoteId] = c.TagIds!; break;
                    case PutNoteTemplate c:
                        if (c.Id is Guid templateId) templates.Remove(Find(templates, x => x.Id == templateId));
                        templates.Add(new(c.Id ?? Guid.NewGuid(), c.Name, c.Title, c.Body, c.Flair, c.TagIds)); break;
                    case DeleteNoteTemplate c: templates.Remove(Find(templates, x => x.Id == c.Id)); break;
                    case PutLibraryTask c:
                        NoteExists(c.NoteId);
                        if (c.Id is Guid taskId) tasks.Remove(Find(tasks, x => x.Id == taskId));
                        tasks.Add(new(c.Id ?? Guid.NewGuid(), c.NoteId, c.Title, c.DueUtc, c.Completed, c.SnoozedUntilUtc)); break;
                    case DeleteLibraryTask c: tasks.Remove(Find(tasks, x => x.Id == c.Id)); break;
                    case CompleteLibraryTask c:
                        var task = Find(tasks, x => x.Id == c.Id); tasks[tasks.IndexOf(task)] = task with { Completed = c.Completed }; break;
                    case SnoozeLibraryTask c:
                        var snoozed = Find(tasks, x => x.Id == c.Id); tasks[tasks.IndexOf(snoozed)] = snoozed with { SnoozedUntilUtc = c.UntilUtc }; break;
                    case AddLibraryAttachment c: LibraryLogic.Require(c.Attachment is not null, "Missing attachment."); NoteExists(c.Attachment!.NoteId); attachments.Add(c.Attachment); break;
                    case RemoveLibraryAttachment c: attachments.Remove(Find(attachments, x => x.Id == c.Id)); break;
                    default: return Outcome.Fail(ErrorCode.Validation, "Unknown library command.");
                }
                var candidate = new LibraryState(1, checked(State.Revision + 1), folders, assignments, tags, noteTags, templates, tasks, attachments);
                LibraryLogic.Validate(candidate); candidate = candidate.Freeze();
                var result = store.Commit(candidate, State.Revision, snapshot);
                if (result.Success) { State = candidate; Warning = result.Warning ?? normalized.Warning; }
                return result;
            }
            catch (DataException ex) { return Outcome.Fail(ex.Code, ex.Message); }
            catch (OverflowException) { return Outcome.Fail(ErrorCode.CapacityExceeded, "Library revision capacity exceeded."); }
        }
    }
}
