using System.Collections.ObjectModel;
namespace Threadbook.Core;

public sealed record LibraryFolder(Guid Id, Guid? ParentId, string Name);
public sealed record LibraryTag(Guid Id, string Name);
public sealed record NoteTemplate(Guid Id, string Name, string Title, string Body, string Flair, IReadOnlyList<Guid> TagIds);
public sealed record LibraryTask(Guid Id, Guid NoteId, string Title, DateTimeOffset? DueUtc, bool Completed, DateTimeOffset? SnoozedUntilUtc);
public sealed record AttachmentInfo(Guid Id, Guid NoteId, string DisplayName, string MediaType, long Length, string Sha256);
public sealed record LibraryState(int SchemaVersion, long Revision, IReadOnlyList<LibraryFolder> Folders, IReadOnlyDictionary<Guid, Guid> NotebookFolders, IReadOnlyList<LibraryTag> Tags, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> NoteTags, IReadOnlyList<NoteTemplate> Templates, IReadOnlyList<LibraryTask> Tasks, IReadOnlyList<AttachmentInfo> Attachments)
{
    public static LibraryState Empty => new LibraryState(1, 0, [], new Dictionary<Guid, Guid>(), [], new Dictionary<Guid, IReadOnlyList<Guid>>(), [], [], []).Freeze();
    public LibraryState Freeze() => this with
    {
        Folders = Array.AsReadOnly(Folders.ToArray()),
        NotebookFolders = new ReadOnlyDictionary<Guid, Guid>(NotebookFolders.ToDictionary()),
        Tags = Array.AsReadOnly(Tags.ToArray()),
        NoteTags = new ReadOnlyDictionary<Guid, IReadOnlyList<Guid>>(NoteTags.ToDictionary(x => x.Key, x => (IReadOnlyList<Guid>)Array.AsReadOnly(x.Value.ToArray()))),
        Templates = Array.AsReadOnly(Templates.Select(x => x with { TagIds = Array.AsReadOnly(x.TagIds.ToArray()) }).ToArray()),
        Tasks = Array.AsReadOnly(Tasks.ToArray()), Attachments = Array.AsReadOnly(Attachments.ToArray())
    };
}
public sealed record LibraryLoadResult(LibraryState State, string? Warning, bool WritesBlocked);
public interface ILibraryStore
{
    LibraryLoadResult Load(Snapshot canonical);
    Outcome Commit(LibraryState candidate, long expectedRevision, Snapshot canonical);
}
public abstract record LibraryCommand;
public sealed record PutLibraryFolder(Guid? Id, Guid? ParentId, string Name) : LibraryCommand;
public sealed record DeleteLibraryFolder(Guid Id) : LibraryCommand;
public sealed record AssignNotebookFolder(Guid NotebookId, Guid? FolderId) : LibraryCommand;
public sealed record PutLibraryTag(Guid? Id, string Name) : LibraryCommand;
public sealed record DeleteLibraryTag(Guid Id) : LibraryCommand;
public sealed record AssignNoteTags(Guid NoteId, IReadOnlyList<Guid> TagIds) : LibraryCommand;
public sealed record PutNoteTemplate(Guid? Id, string Name, string Title, string Body, string Flair, IReadOnlyList<Guid> TagIds) : LibraryCommand;
public sealed record DeleteNoteTemplate(Guid Id) : LibraryCommand;
public sealed record PutLibraryTask(Guid? Id, Guid NoteId, string Title, DateTimeOffset? DueUtc, bool Completed = false, DateTimeOffset? SnoozedUntilUtc = null) : LibraryCommand;
public sealed record DeleteLibraryTask(Guid Id) : LibraryCommand;
public sealed record CompleteLibraryTask(Guid Id, bool Completed) : LibraryCommand;
public sealed record SnoozeLibraryTask(Guid Id, DateTimeOffset? UntilUtc) : LibraryCommand;
public sealed record AddLibraryAttachment(AttachmentInfo Attachment) : LibraryCommand;
public sealed record RemoveLibraryAttachment(Guid Id) : LibraryCommand;
public static class LibraryTemplates
{
    public static IReadOnlyList<NoteTemplate> BuiltIns { get; } = Array.AsReadOnly(new[]
    {
        new NoteTemplate(Guid.Parse("6036bcf8-3b5f-42f8-a47f-000000000001"), "Meeting", "Meeting notes", "## Agenda\n\n## Notes\n\n## Actions\n- [ ] Follow up\n", "", Array.Empty<Guid>()),
        new NoteTemplate(Guid.Parse("6036bcf8-3b5f-42f8-a47f-000000000002"), "Journal", "Journal", "## Today\n\n## Reflection\n\n## Tomorrow\n", "", Array.Empty<Guid>()),
        new NoteTemplate(Guid.Parse("6036bcf8-3b5f-42f8-a47f-000000000003"), "Project", "Project", "## Goal\n\n## Milestones\n- [ ] First milestone\n\n## Resources\n", "", Array.Empty<Guid>()),
        new NoteTemplate(Guid.Parse("6036bcf8-3b5f-42f8-a47f-000000000004"), "Checklist", "Checklist", "- [ ] First task\n- [ ] Second task\n", "", Array.Empty<Guid>())
    });
}
