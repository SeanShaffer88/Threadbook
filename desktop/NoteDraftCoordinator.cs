using System.Windows.Threading;
using Threadbook.Core;
using Threadbook.Storage;
namespace Threadbook.Desktop;

// One UI-thread lease for schema 1's single recovery slot. No filesystem defaults.
internal sealed class NoteDraftCoordinator : IDisposable
{
    private readonly RecoveryStore recovery;
    private readonly Func<bool> isUiUnlocked;
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private InlineEditSession? owner;
    private bool disposed;
    public NoteDraftCoordinator(RecoveryStore recovery, Func<bool> isUiUnlocked)
    {
        this.recovery = recovery; this.isUiUnlocked = isUiUnlocked; debounce.Tick += Tick;
    }
    private static Outcome Blocked(string message) => Outcome.Fail(ErrorCode.Conflict, message);
    private Outcome Guard(InlineEditSession session)
    {
        if (disposed || !isUiUnlocked()) return Blocked("The vault is locked or this editor is no longer active.");
        if (!ReferenceEquals(owner, session)) return Blocked("This editor does not own the recovery slot.");
        return Outcome.Ok();
    }
    public Outcome TryAcquire(InlineEditSession session)
    {
        if (disposed || !isUiUnlocked()) return Blocked("The vault is locked.");
        if (owner is not null && !ReferenceEquals(owner, session)) return Blocked("Resolve this draft before creating another note.");
        try {
            var existing = recovery.LoadDraft();
            if (existing is not null && existing.DraftId != session.DraftId) return Blocked("Review or discard the retained draft before creating another note.");
            if (owner is null) { owner = session; owner.StateChanged += Changed; }
            return Outcome.Ok();
        } catch (DataException ex) { return Outcome.Fail(ex.Code, ex.Message); }
    }
    private void Changed(object? sender, EventArgs e)
    {
        debounce.Stop();
        if (!disposed && isUiUnlocked() && owner is { IsDirty: true, State: not NoteEditState.SuspendedLocked }) debounce.Start();
    }
    private void Tick(object? sender, EventArgs e)
    {
        debounce.Stop();
        if (disposed || !isUiUnlocked()) return;
        if (owner is not null) { var result = Flush(owner); if (!result.Success) owner.Surface.Error.Text = result.Error!.Message; }
    }
    public Outcome Flush(InlineEditSession session)
    {
        debounce.Stop(); var guard = Guard(session); if (!guard.Success) return guard;
        try {
            var existing = recovery.LoadDraft();
            if (existing is not null && existing.DraftId != session.DraftId) return Blocked("Another draft owns recovery; its bytes were preserved.");
            return session.IsDirty ? recovery.SaveDraft(session.CaptureDraft()) : Outcome.Ok();
        } catch (DataException ex) { return Outcome.Fail(ex.Code, ex.Message); }
    }
    internal Outcome BindIdentity(InlineEditSession session, Guid noteId)
    {
        debounce.Stop(); var guard = Guard(session); if (!guard.Success) return guard;
        if (noteId == Guid.Empty) return Outcome.Fail(ErrorCode.Validation, "Note identity required.");
        if (session.EffectiveNoteId is Guid bound && bound != noteId) return Blocked("Replacing a bound identity requires explicit authorization.");
        try {
            var existing = recovery.LoadDraft();
            if (existing is not null && existing.DraftId != session.DraftId) return Blocked("Another draft owns recovery.");
            // Force persistence even for a clean surface; publish only after durable success.
            var result = recovery.SaveDraft(session.CaptureDraft() with { NoteId = noteId });
            if (result.Success) session.BindIdentity(noteId);
            return result;
        } catch (DataException ex) { return Outcome.Fail(ex.Code, ex.Message); }
    }
    public Outcome Complete(InlineEditSession session) => Cleanup(session);
    public Outcome Discard(InlineEditSession session) => Cleanup(session);
    private Outcome Cleanup(InlineEditSession session)
    {
        debounce.Stop(); var guard = Guard(session); if (!guard.Success) return guard;
        try {
            var existing = recovery.LoadDraft();
            if (existing is not null && existing.DraftId != session.DraftId) return Blocked("Another draft owns recovery; cleanup was refused.");
            var result = existing is null ? Outcome.Ok() : recovery.DiscardDraft();
            if (result.Success) Release(session);
            return result;
        } catch (DataException ex) { return Outcome.Fail(ex.Code, ex.Message); }
    }
    // Ordinary modal close keeps the on-disk lease. A later acquisition must review it.
    public void Release(InlineEditSession session)
    {
        if (!ReferenceEquals(owner, session)) return;
        debounce.Stop(); owner.StateChanged -= Changed; owner = null;
    }
    public Outcome FlushAll()
    {
        debounce.Stop(); if (owner is null) return Outcome.Ok();
        var result = Flush(owner); if (result.Success) owner.Suspend(); else ResumeAll(); return result;
    }
    public void ResumeAll() { if (disposed || !isUiUnlocked()) return; owner?.Resume(); Changed(this, EventArgs.Empty); }
    public void Dispose() { if (disposed) return; disposed = true; debounce.Stop(); debounce.Tick -= Tick; if (owner is not null) owner.StateChanged -= Changed; owner = null; }
}
