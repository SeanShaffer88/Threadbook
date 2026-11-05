using Threadbook.Core;
namespace Threadbook.Storage;

/// <summary>Caller-serialized recoverable mutation gateway. Multi-file writes are not atomic.</summary>
public sealed class ArchiveMutationService
{
    private readonly JsonStore owner;
    private readonly NotebookService notebook;
    private readonly LibraryStore library;
    private readonly ArchiveStore archive;
    private readonly object gate = new();
    private bool inBeforePrepare;
    private void RejectCallbackReentry() {
        if (inBeforePrepare) throw new DataException(ErrorCode.Conflict, "Recovery callback cannot reenter the mutation gateway.");
    }
    public Action<string>? BeforePhase { get; set; }
    public Snapshot State => notebook.State;
    public LibraryState LibraryState => library.ReadRaw();
    public ArchiveMutationService(JsonStore owner, NotebookService notebook) { this.owner = owner; this.notebook = notebook; library = new(owner); archive = new(owner); }
    private sealed record Journal(int SchemaVersion, Guid Id, string BeforeCanonical, string AfterCanonical, string BeforeLibrary, string AfterLibrary, ArchiveReference Canonical, ArchiveReference Library, Guid? RestoreEntryId, IReadOnlyList<ArchiveReference> Required, bool CleanupReady = false);
    private static string Fingerprint<T>(T data, int max) => ArchiveStore.Hash(ArchiveStore.Encode(data, max));
    internal static void ValidateJournal(JsonStore owner) { _ = ReadJournal(owner); }
    private static Journal ReadJournal(JsonStore owner)
    {
        var j = ArchiveStore.Decode<Journal>(owner.Files.Read("archive-operation.json", ArchiveStore.DescriptorLimit));
        if (j.SchemaVersion != 1) throw new DataException(ErrorCode.UnsupportedVersion, "Unsupported archive operation schema.");
        ArchiveStore.Require(j.Id != Guid.Empty && j.Canonical is not null && j.Library is not null && j.Canonical.Key == $"archive-stage-{j.Id:N}.json" && j.Library.Key == $"archive-library-{j.Id:N}.json", "Invalid operation stage keys.");
        foreach (var hash in new[] { j.BeforeCanonical, j.AfterCanonical, j.BeforeLibrary, j.AfterLibrary, j.Canonical.Sha256, j.Library.Sha256 }) ArchiveStore.Require(hash is { Length: 64 } && hash.All(Uri.IsHexDigit), "Invalid operation fingerprint.");
        var store = new ArchiveStore(owner);
        ArchiveStore.Require(j.Required is { Count: <= ArchiveStore.MaxEntries }, "Missing required preservation closure.");
        var identities = new HashSet<Guid>();
        foreach (var reference in j.Required) {
            ArchiveStore.Require(reference is not null && reference.Key is not null && System.Text.RegularExpressions.Regex.IsMatch(reference.Key, @"\Aarchive-entry-[0-9a-f]{32}\.json\z"), "Invalid required descriptor identity.");
            var entryId = Guid.ParseExact(reference.Key[14..^5], "N");
            ArchiveStore.Require(identities.Add(entryId), "Duplicate required descriptor.");
            _ = store.Verify(reference); _ = store.ReadArchive(entryId);
        }
        ArchiveStore.Require(j.RestoreEntryId is null || identities.Contains(j.RestoreEntryId.Value), "Restore archive is not journal bound.");
        if (!j.CleanupReady) { _ = JsonStore.Parse(store.Verify(j.Canonical)); _ = LibraryStore.Parse(store.Verify(j.Library)); } return j;
    }
    private void Ready(long canonicalRevision, long libraryRevision)
    {
        RejectCallbackReentry(); owner.AssertLiveOwner(); archive.ValidateAll();
        if (owner.Files.Exists("archive-operation.json")) throw new DataException(ErrorCode.Conflict, "Archive operation pending; reconcile or cancel it first.");
        var actual = owner.Load();
        if (actual.Revision != canonicalRevision || State.Revision != canonicalRevision || Fingerprint(actual, JsonStore.MaxBytes) != Fingerprint(State, JsonStore.MaxBytes) || LibraryState.Revision != libraryRevision)
            throw new DataException(ErrorCode.Conflict, "Canonical or library revision changed; reload first.");
    }
    public void Reload() { lock (gate) { RejectCallbackReentry(); owner.AssertLiveOwner(); notebook.Reload(); _ = library.ReadRaw(); } }
    public IReadOnlyList<ArchiveEntry> ListHistory(Guid noteId) { lock (gate) return Array.AsReadOnly(archive.List().Where(e => e.Kind == ArchiveKind.History && e.NoteIds.Contains(noteId)).ToArray()); }
    public IReadOnlyList<ArchiveEntry> ListTrash() { lock (gate) return Array.AsReadOnly(archive.List().Where(e => e.Kind == ArchiveKind.Trash).ToArray()); }
    public ArchiveBundle ReadArchive(Guid entryId) { lock (gate) return archive.ReadArchive(entryId); }
    public Outcome PermanentlyDeleteArchive(Guid entryId) { lock (gate) { RejectCallbackReentry(); return archive.PermanentlyDeleteArchive(entryId); } }
    private static LibraryState SliceLibrary(LibraryState source, Snapshot data)
    {
        var notes = data.Notes.Select(n => n.Id).ToHashSet(); var books = data.Notebooks.Select(b => b.Id).ToHashSet();
        var tagIds = source.NoteTags.Where(p => notes.Contains(p.Key)).SelectMany(p => p.Value).ToHashSet();
        var assignments = source.NotebookFolders.Where(p => books.Contains(p.Key)).ToDictionary();
        var folderIndex = source.Folders.ToDictionary(f => f.Id); var folderIds = assignments.Values.ToHashSet();
        foreach (var assigned in assignments.Values) {
            var id = assigned;
            while (folderIndex.TryGetValue(id, out var folder) && folder.ParentId is Guid parent && folderIds.Add(parent)) id = parent;
        }
        return (source with { Folders = source.Folders.Where(f => folderIds.Contains(f.Id)).ToArray(), Revision = 0, NoteTags = source.NoteTags.Where(p => notes.Contains(p.Key)).ToDictionary(), Tags = source.Tags.Where(t => tagIds.Contains(t.Id)).ToArray(), Templates = [], Tasks = source.Tasks.Where(t => notes.Contains(t.NoteId)).ToArray(), Attachments = source.Attachments.Where(a => notes.Contains(a.NoteId)).ToArray(), NotebookFolders = source.NotebookFolders.Where(p => books.Contains(p.Key)).ToDictionary() }).Freeze();
    }
    private IReadOnlyList<ArchiveReference> Protect(Snapshot before, Snapshot after, LibraryState metadata)
    {
        var required = new List<ArchiveReference>();
        var afterNotes = after.Notes.ToDictionary(n => n.Id); var afterBooks = after.Notebooks.Select(b => b.Id).ToHashSet();
        var deletedBooks = before.Notebooks.Where(b => !afterBooks.Contains(b.Id)).Select(b => b.Id).ToHashSet();
        var beforeBooks = before.Notebooks.ToDictionary(b => b.Id);
        var deleted = before.Notes.Where(n => !afterNotes.ContainsKey(n.Id)).ToArray();
        var changed = before.Notes.Where(n => afterNotes.TryGetValue(n.Id, out var x) && (x.Title != n.Title || x.Body != n.Body || x.Flair != n.Flair || x.NotebookId != n.NotebookId)).ToArray();
        void Capture(ArchiveKind kind, Note[] notes) {
            if (notes.Length == 0 && !(kind == ArchiveKind.Trash && deletedBooks.Count > 0)) return;
            var ids = notes.Select(n => n.Id).ToHashSet(); var bookIds = notes.Select(n => n.NotebookId).ToHashSet(); if (kind == ArchiveKind.Trash) bookIds.UnionWith(deletedBooks); var books = bookIds.Select(id => beforeBooks[id]).ToArray();
            // History preserves content only; Trash must preserve the complete deleted graph and metadata.
            var data = new Snapshot(1, 0, books, notes, kind == ArchiveKind.Trash ? before.Replies.Where(r => ids.Contains(r.NoteId)).ToArray() : []).Freeze();
            BeforePhase?.Invoke("archive"); var entry = archive.Capture(kind, data, kind == ArchiveKind.Trash ? SliceLibrary(metadata, data) : LibraryState.Empty); required.Add(DescriptorReference(entry.Id));
        }
        Capture(ArchiveKind.Trash, deleted);
        foreach (var n in changed) Capture(ArchiveKind.History, [n]);
        return required;
    }
    public Outcome Execute(Command command, long expectedCanonicalRevision, long expectedLibraryRevision)
        => ExecuteCore(command, expectedCanonicalRevision, expectedLibraryRevision, null);
    /// <summary>Persist recovery identity once before preservation or journal preparation. The callback must not mutate stores or reenter this gateway.</summary>
    public Outcome Execute(Command command, long expectedCanonicalRevision, long expectedLibraryRevision, Func<Snapshot, Outcome> beforePrepare)
    {
        ArgumentNullException.ThrowIfNull(beforePrepare);
        return ExecuteCore(command, expectedCanonicalRevision, expectedLibraryRevision, beforePrepare);
    }
    private Outcome ExecuteCore(Command command, long expectedCanonicalRevision, long expectedLibraryRevision, Func<Snapshot, Outcome>? beforePrepare)
    {
        lock (gate) return Run(() => {
            Ready(expectedCanonicalRevision, expectedLibraryRevision); var preview = notebook.Preview(command, expectedCanonicalRevision); ArchiveStore.Check(preview.Outcome);
            var prior = LibraryState; var candidate = preview.Candidate!;
            if (beforePrepare is not null) {
                var canonicalIdentity = Fingerprint(preview.Before, JsonStore.MaxBytes);
                var libraryIdentity = Fingerprint(prior, LibraryStore.MaxBytes);
                // The candidate is frozen and is never regenerated after binding its identity.
                inBeforePrepare = true;
                try { ArchiveStore.Check(beforePrepare(candidate)); }
                finally { inBeforePrepare = false; }
                Ready(expectedCanonicalRevision, expectedLibraryRevision);
                if (Fingerprint(State, JsonStore.MaxBytes) != canonicalIdentity || Fingerprint(LibraryState, LibraryStore.MaxBytes) != libraryIdentity)
                    throw new DataException(ErrorCode.Conflict, "Stores changed during recovery binding.");
            }
            var required = Protect(preview.Before, candidate, prior);
            var next = LibraryLogic.Normalize(prior, candidate).State with { Revision = checked(prior.Revision + 1) };
            Prepare(candidate, next, prior, null, required); ArchiveStore.Check(ReconcilePending());
        });
    }
    private Outcome Run(Action action)
    {
        try { action(); return Outcome.Ok(); }
        catch (DataException ex) { return Outcome.Fail(ex.Code, ex.Message); }
        catch (OverflowException) { return Outcome.Fail(ErrorCode.CapacityExceeded, "Revision capacity exceeded."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Outcome.Fail(ErrorCode.StorageFailure, "Operation interrupted; inspect pending operation before retrying."); }
    }
    private ArchiveReference DescriptorReference(Guid id) {
        var key = ArchiveStore.EntryKey(id); var bytes = owner.Files.Read(key, ArchiveStore.DescriptorLimit);
        return new(key, bytes.Length, ArchiveStore.Hash(bytes));
    }
    private void Prepare(Snapshot candidate, LibraryState next, LibraryState prior, Guid? restoreId, IReadOnlyList<ArchiveReference> required)
    {
        Validation.Check(candidate); LibraryLogic.Validate(next);
        ArchiveStore.Require(LibraryLogic.Normalize(next, candidate).Warning is null, "Candidate metadata contains missing references.");
        var id = Guid.NewGuid(); var canonicalKey = $"archive-stage-{id:N}.json"; var libraryKey = $"archive-library-{id:N}.json";
        var canonicalBytes = ArchiveStore.Encode(candidate, JsonStore.MaxBytes); var libraryBytes = ArchiveStore.Encode(next, LibraryStore.MaxBytes);
        var journal = new Journal(1, id, Fingerprint(State, JsonStore.MaxBytes), ArchiveStore.Hash(canonicalBytes), Fingerprint(prior, LibraryStore.MaxBytes), ArchiveStore.Hash(libraryBytes), new(canonicalKey, canonicalBytes.Length, ArchiveStore.Hash(canonicalBytes)), new(libraryKey, libraryBytes.Length, ArchiveStore.Hash(libraryBytes)), restoreId, required);
        var bytes = ArchiveStore.Encode(journal, ArchiveStore.DescriptorLimit);
        archive.EnsureCapacity(new Dictionary<string, byte[]> { [canonicalKey] = canonicalBytes, [libraryKey] = libraryBytes, ["archive-operation.json"] = bytes });
        BeforePhase?.Invoke("stages"); ArchiveStore.Check(owner.Files.WriteAtomic(canonicalKey, canonicalBytes, JsonStore.MaxBytes, false)); ArchiveStore.Check(owner.Files.WriteAtomic(libraryKey, libraryBytes, LibraryStore.MaxBytes, false));
        _ = archive.Verify(journal.Canonical); _ = archive.Verify(journal.Library);
        BeforePhase?.Invoke("journal"); ArchiveStore.Check(owner.Files.WriteAtomic("archive-operation.json", bytes, ArchiveStore.DescriptorLimit, false)); _ = ReadJournal(owner);
    }
    public Outcome ReconcilePending()
    {
        lock (gate) return Run(() => {
            RejectCallbackReentry(); owner.AssertLiveOwner(); if (!owner.Files.Exists("archive-operation.json")) return;
            var j = ReadJournal(owner); var canonical = owner.Load(); var metadata = LibraryState;
            var cf = Fingerprint(canonical, JsonStore.MaxBytes); var lf = Fingerprint(metadata, LibraryStore.MaxBytes);
            if ((cf != j.BeforeCanonical && cf != j.AfterCanonical) || (lf != j.BeforeLibrary && lf != j.AfterLibrary) || (cf == j.BeforeCanonical && lf == j.AfterLibrary)) throw new DataException(ErrorCode.Conflict, "Pending operation conflicts with current files; preserved for resolution.");
            if (j.CleanupReady) {
                ArchiveStore.Require(cf == j.AfterCanonical && lf == j.AfterLibrary, "Cleanup journal conflicts with committed state.");
                Cleanup(j); notebook.Reload(); return;
            }
            var target = JsonStore.Parse(archive.Verify(j.Canonical)); var next = LibraryStore.Parse(archive.Verify(j.Library));
            if (j.RestoreEntryId is Guid entryId) {
                var bundle = archive.ReadArchive(entryId);
                foreach (var a in bundle.Library.Attachments) {
                    var blob = archive.Verify(bundle.Entry.Files.Single(r => r.Key == ArchiveStore.BlobKey(a.Id)));
                    if (owner.Files.Exists(AttachmentStore.Key(a.Id))) ArchiveStore.Require(ArchiveStore.Hash(owner.Files.Read(AttachmentStore.Key(a.Id), AttachmentStore.MaxFileBytes)) == ArchiveStore.Hash(blob), "Attachment collision during restore.");
                    else { BeforePhase?.Invoke("blob"); ArchiveStore.Check(owner.Files.WriteAtomic(AttachmentStore.Key(a.Id), blob, AttachmentStore.MaxFileBytes, false)); }
                }
            }
            if (cf == j.BeforeCanonical) { BeforePhase?.Invoke("canonical"); ArchiveStore.Check(owner.Commit(target, canonical.Revision)); }
            notebook.Reload();
            if (lf == j.BeforeLibrary) { BeforePhase?.Invoke("library"); ArchiveStore.Check(library.Commit(next, metadata.Revision, notebook.State)); }
            ArchiveStore.Require(Fingerprint(owner.Load(), JsonStore.MaxBytes) == j.AfterCanonical && Fingerprint(LibraryState, LibraryStore.MaxBytes) == j.AfterLibrary, "Operation results could not be verified.");
            BeforePhase?.Invoke("finalize");
            var completed = j with { CleanupReady = true };
            ArchiveStore.Check(owner.Files.WriteAtomic("archive-operation.json", ArchiveStore.Encode(completed, ArchiveStore.DescriptorLimit), ArchiveStore.DescriptorLimit, false));
            _ = ReadJournal(owner); Cleanup(completed);
        });
    }
    private void Cleanup(Journal journal) {
        ArchiveStore.Check(owner.Files.Delete(journal.Canonical.Key)); ArchiveStore.Check(owner.Files.Delete(journal.Library.Key));
        ArchiveStore.Check(owner.Files.Delete("archive-operation.json"));
    }
    public Outcome CancelPending()
    {
        lock (gate) return Run(() => {
            RejectCallbackReentry(); owner.AssertLiveOwner(); if (!owner.Files.Exists("archive-operation.json")) return;
            var j = ReadJournal(owner);
            if (Fingerprint(owner.Load(), JsonStore.MaxBytes) != j.BeforeCanonical || Fingerprint(LibraryState, LibraryStore.MaxBytes) != j.BeforeLibrary) throw new DataException(ErrorCode.Conflict, "An applied operation cannot be cancelled; reconcile it.");
            ArchiveStore.Check(owner.Files.Delete("archive-operation.json")); ArchiveStore.Check(owner.Files.Delete(j.Canonical.Key)); ArchiveStore.Check(owner.Files.Delete(j.Library.Key));
        });
    }
    public ArchiveRestorePlan PrepareRestore(Guid entryId, Guid? destinationNotebookId, long expectedCanonicalRevision, long expectedLibraryRevision)
    {
        lock (gate) { Ready(expectedCanonicalRevision, expectedLibraryRevision); var plan = new ArchiveRestorePlan(entryId, destinationNotebookId, expectedCanonicalRevision, expectedLibraryRevision); _ = BuildRestore(plan); return plan; }
    }
    private (Snapshot Data, LibraryState Library) BuildRestore(ArchiveRestorePlan plan)
    {
        var bundle = archive.ReadArchive(plan.EntryId); var current = State; var meta = LibraryState;
        if (bundle.Entry.Kind == ArchiveKind.History) {
            var old = bundle.Data.Notes.Single(); var live = current.Notes.SingleOrDefault(n => n.Id == old.Id) ?? throw new DataException(ErrorCode.NotFound, "History target is absent.");
            var book = plan.DestinationNotebookId ?? live.NotebookId;
            var restored = live with { Title = old.Title, Body = old.Body, Flair = old.Flair, NotebookId = book, Updated = DateTimeOffset.UtcNow };
            var result = current with { Revision = checked(current.Revision + 1), Notes = current.Notes.Select(n => n.Id == live.Id ? restored : n).ToArray() }; Validation.Check(result);
            return (result.Freeze(), meta with { Revision = checked(meta.Revision + 1) });
        }
        var allIds = current.Notebooks.Select(b => b.Id).Concat(current.Notes.Select(n => n.Id)).Concat(current.Replies.Select(r => r.Id)).ToHashSet();
        if (bundle.Data.Notes.Any(n => allIds.Contains(n.Id)) || bundle.Data.Replies.Any(r => allIds.Contains(r.Id))) throw new DataException(ErrorCode.Conflict, "Restore ID collides with an active record.");
        var books = current.Notebooks.ToList(); var notes = bundle.Data.Notes.ToArray();
        if (plan.DestinationNotebookId is Guid destination) { if (!books.Any(b => b.Id == destination)) throw new DataException(ErrorCode.NotFound, "Destination notebook is absent."); notes = notes.Select(n => n with { NotebookId = destination }).ToArray(); }
        else foreach (var book in bundle.Data.Notebooks) {
            var existing = books.FirstOrDefault(b => b.Id == book.Id);
            if (existing is not null && existing != book) throw new DataException(ErrorCode.Conflict, "Notebook definition changed; choose a destination.");
            if (existing is null) { if (allIds.Contains(book.Id)) throw new DataException(ErrorCode.Conflict, "Notebook ID collision."); books.Add(book); }
        }
        var data = (current with { Revision = checked(current.Revision + 1), Notebooks = books, Notes = current.Notes.Concat(notes).ToArray(), Replies = current.Replies.Concat(bundle.Data.Replies).ToArray() }).Freeze(); Validation.Check(data);
        T[] Merge<T>(IReadOnlyList<T> live, IReadOnlyList<T> captured, Func<T, Guid> id, bool allowEqual) {
            var output = live.ToList(); foreach (var item in captured) { var found = live.FirstOrDefault(x => id(x) == id(item)); if (found is not null) { if (!allowEqual || !EqualityComparer<T>.Default.Equals(found, item)) throw new DataException(ErrorCode.Conflict, "Metadata identity collision."); } else output.Add(item); } return output.ToArray();
        }
        var tags = meta.NoteTags.ToDictionary(); foreach (var p in bundle.Library.NoteTags) if (!tags.TryAdd(p.Key, p.Value)) throw new DataException(ErrorCode.Conflict, "Note-tag collision.");
        var folders = meta.NotebookFolders.ToDictionary(); if (plan.DestinationNotebookId is null) foreach (var p in bundle.Library.NotebookFolders) { if (folders.TryGetValue(p.Key, out var value) && value != p.Value) throw new DataException(ErrorCode.Conflict, "Notebook folder collision."); folders[p.Key] = p.Value; }
        var next = (meta with { Revision = checked(meta.Revision + 1), Folders = Merge(meta.Folders, plan.DestinationNotebookId is null ? bundle.Library.Folders : [], f => f.Id, true), NotebookFolders = folders, Tags = Merge(meta.Tags, bundle.Library.Tags, t => t.Id, true), NoteTags = tags, Tasks = Merge(meta.Tasks, bundle.Library.Tasks, t => t.Id, false), Attachments = Merge(meta.Attachments, bundle.Library.Attachments, a => a.Id, false) }).Freeze(); LibraryLogic.Validate(next);
        foreach (var a in bundle.Library.Attachments) if (owner.Files.Exists(AttachmentStore.Key(a.Id))) {
            // A prior deletion intentionally retains its active blob; equal bytes are safe to reuse.
            var bytes = owner.Files.Read(AttachmentStore.Key(a.Id), AttachmentStore.MaxFileBytes); ArchiveStore.Require(bytes.LongLength == a.Length && ArchiveStore.Hash(bytes).Equals(a.Sha256, StringComparison.OrdinalIgnoreCase), "Attachment blob collision.");
        }
        return (data, next);
    }
    public Outcome Restore(ArchiveRestorePlan plan)
    {
        lock (gate) return Run(() => {
            Ready(plan.ExpectedCanonicalRevision, plan.ExpectedLibraryRevision); var result = BuildRestore(plan); var prior = LibraryState;
            var required = Protect(State, result.Data, prior).Append(DescriptorReference(plan.EntryId)).DistinctBy(r => r.Key).ToArray(); Prepare(result.Data, result.Library, prior, plan.EntryId, required); ArchiveStore.Check(ReconcilePending());
        });
    }
}
