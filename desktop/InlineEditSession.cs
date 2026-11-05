using Threadbook.Core;
using Threadbook.Storage;
namespace Threadbook.Desktop;

internal sealed record NoteEditFields(Guid NotebookId, string Title, string Body, string Flair)
{
    internal NoteEditFields Normalized => this with { Title = Title.Trim(), Flair = Flair.Trim() };
    internal static NoteEditFields From(Note note) => new(note.NotebookId, note.Title, note.Body, note.Flair);
}
internal sealed record NoteContentToken(Guid NoteId, NoteEditFields Original);
internal enum NoteEditState { Reading, EditingClean, EditingDirty, Saving, SuspendedLocked, Conflict }
internal sealed record NoteSaveResult(Outcome Outcome, Guid? NoteId, bool MetadataPending);
internal enum LeaveReason { Navigation, Search, Filter, Layout, Workspace, NewNote, ModalEditor, Delete, Restore, VaultSwitch, Close, CancelEdit }

internal sealed class InlineEditSession : IDisposable
{
    private readonly long baseRevision;
    private NoteEditFields baseline;
    private bool restored, disposed;
    public Guid DraftId { get; }
    public NoteContentToken? Token { get; private set; }
    private Guid? boundNoteId;
    public Guid? EffectiveNoteId => boundNoteId ?? Token?.NoteId;
    // Identity alone is not evidence that content has committed.
    internal void BindIdentity(Guid noteId) {
        if (noteId == Guid.Empty) throw new ArgumentException("Note identity required.");
        boundNoteId = noteId;
    }
    internal void AuthorizeReplacement() { boundNoteId = null; Token = null; }
    public NoteEditingSurface Surface { get; }
    public NoteEditState State { get; private set; }
    public bool IsDirty => restored || Surface.Fields.Normalized != baseline.Normalized;
    public event EventHandler? StateChanged;
    public InlineEditSession(Guid draftId, NoteContentToken? token, long baseRevision, NoteEditingSurface surface, bool restored)
    {
        if (draftId == Guid.Empty) throw new ArgumentException("Draft identity is required.", nameof(draftId));
        DraftId = draftId; Token = token; this.baseRevision = baseRevision; Surface = surface; this.restored = restored;
        baseline = surface.Fields; State = restored ? NoteEditState.EditingDirty : NoteEditState.EditingClean;
        Surface.FieldsChanged += Changed;
    }
    private void Changed(object? sender, EventArgs e)
    {
        if (disposed || State == NoteEditState.SuspendedLocked) return;
        State = IsDirty ? NoteEditState.EditingDirty : NoteEditState.EditingClean; StateChanged?.Invoke(this, EventArgs.Empty);
    }
    public RecoveryDraft CaptureDraft()
    {
        var fields = Surface.Fields;
        return new(1, DraftId, fields.NotebookId, EffectiveNoteId, baseRevision, fields.Title, fields.Body, fields.Flair, DateTimeOffset.UtcNow);
    }
    public void MarkSaved(Guid noteId, NoteContentToken token)
    {
        if (token.NoteId != noteId) throw new ArgumentException("Mismatched note identity.", nameof(token));
        Token = token; baseline = token.Original; restored = false; Changed(this, EventArgs.Empty);
    }
    public void MarkConflict() { State = NoteEditState.Conflict; StateChanged?.Invoke(this, EventArgs.Empty); }
    public void Suspend() { State = NoteEditState.SuspendedLocked; Surface.SetInteractionEnabled(false); StateChanged?.Invoke(this, EventArgs.Empty); }
    public void Resume() { Surface.SetInteractionEnabled(true); State = IsDirty ? NoteEditState.EditingDirty : NoteEditState.EditingClean; StateChanged?.Invoke(this, EventArgs.Empty); }
    public void Dispose() { if (disposed) return; disposed = true; Surface.FieldsChanged -= Changed; StateChanged = null; Surface.Dispose(); }
}
