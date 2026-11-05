namespace Threadbook.Core;

public sealed record CommandPreview(Outcome Outcome, Snapshot Before, Snapshot? Candidate);
public enum ArchiveKind { History, Trash }
public sealed record ArchiveReference(string Key, long Length, string Sha256);
public sealed record ArchiveEntry(int SchemaVersion, Guid Id, ArchiveKind Kind, DateTimeOffset CapturedUtc, IReadOnlyList<Guid> NoteIds, IReadOnlyList<ArchiveReference> Files);
public sealed record ArchiveBundle(ArchiveEntry Entry, Snapshot Data, LibraryState Library);
public sealed record ArchiveRestorePlan(Guid EntryId, Guid? DestinationNotebookId, long ExpectedCanonicalRevision, long ExpectedLibraryRevision);
