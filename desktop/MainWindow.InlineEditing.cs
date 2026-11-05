using System.Windows;
using System.Windows.Controls;
using Threadbook.Core;
namespace Threadbook.Desktop;

public sealed partial class MainWindow
{
    private NoteDraftCoordinator? draftCoordinator;
    internal NoteDraftCoordinator DraftCoordinator => draftCoordinator ??= new(recovery, () => InlineUiUnlocked);
    internal bool InlineUiUnlocked => !vaultLocked && store.Files.IsUnlocked;
    private InlineEditSession? inlineSession;
    private Func<MessageBoxResult>? confirmNoteLeaveForTest = null;
    private string appliedSearch = "";
    private bool changingSearch;
    private int appliedSearchStart, appliedSearchLength;
    private void SearchSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!changingSearch && search.Text == appliedSearch) { appliedSearchStart = search.SelectionStart; appliedSearchLength = search.SelectionLength; }
    }
    private void SetSearch(string value)
    {
        changingSearch = true;
        try { search.Text = value; appliedSearch = value; }
        finally { changingSearch = false; }
    }
    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (changingSearch) return;
        string proposed = search.Text; int caret = search.CaretIndex;
        SetSearch(appliedSearch);
        search.Select(Math.Min(appliedSearchStart, appliedSearch.Length), Math.Min(appliedSearchLength, Math.Max(0, appliedSearch.Length - appliedSearchStart)));
        if (!TryLeave(LeaveReason.Search)) return;
        SetSearch(proposed); search.CaretIndex = Math.Min(caret, proposed.Length);
        feedLimit = 75; searchDelay.Stop(); searchDelay.Start();
    }
    private readonly StackPanel inlineHeader = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(12, 0, 12, 0) };
    private readonly DockPanel inlineHost = new() { LastChildFill = true, Visibility = Visibility.Collapsed, MinWidth = 0 };
    private readonly Dictionary<Guid, NoteEditFields> pendingEditFields = new();
    private NoteContentToken BeginEdit(Guid noteId)
    {
        var note = app.State.Notes.Single(n => n.Id == noteId);
        return new(noteId, NoteEditFields.From(note).Normalized);
    }
    internal NoteSaveResult SaveEdit(NoteContentToken token, NoteEditFields fields, InlineEditSession? session = null)
    {
        if (!InlineUiUnlocked) return new(Outcome.Fail(ErrorCode.Conflict, "Vault locked."), token.NoteId, false);
        var current = app.State.Notes.FirstOrDefault(n => n.Id == token.NoteId);
        if (current is null) return new(Outcome.Fail(ErrorCode.NotFound, "Original note is missing. Create a new note explicitly to retain this draft."), token.NoteId, false);
        var actual = NoteEditFields.From(current).Normalized;
        if (pendingEditFields.TryGetValue(token.NoteId, out var intended)) {
            if (ArchivePending) return new(Outcome.Fail(ErrorCode.Conflict, "Complete the pending operation in History or Trash, then retry Save."), token.NoteId, true);
            if (actual != intended) return new(Outcome.Fail(ErrorCode.Conflict, "The saved note changed while metadata completion was pending."), token.NoteId, false);
            pendingEditFields.Remove(token.NoteId);
            // Do not repeat the content write/history capture after metadata reconciliation.
            if (fields.Normalized == intended) return new(Outcome.Ok(), token.NoteId, false);
            token = new(token.NoteId, intended);
            session?.MarkSaved(token.NoteId, token);
        }
        if (actual != token.Original.Normalized) return new(Outcome.Fail(ErrorCode.Conflict, "This note's content changed. Your draft is retained; review the saved note before retrying."), token.NoteId, false);
        if (!ArchiveReady()) return new(Outcome.Fail(ErrorCode.Conflict, "Storage is unavailable; draft retained."), token.NoteId, ArchivePending);
        pendingEditFields[token.NoteId] = fields.Normalized;
        Outcome outcome;
        try { outcome = archiveMutations.Execute(new PutNote(token.NoteId, fields.NotebookId, fields.Title, fields.Body, fields.Flair), app.State.Revision, archiveMutations.LibraryState.Revision); }
        catch (Exception ex) when (ArchiveError(ex)) { outcome = Outcome.Fail(ErrorCode.StorageFailure, ex.Message); }
        bool success = CompleteArchiveMutation(outcome);
        bool pending = ArchivePending;
        if (success || !pending) pendingEditFields.Remove(token.NoteId);
        return new(success ? Outcome.Ok() : outcome.Success ? Outcome.Fail(ErrorCode.StorageFailure, "Reload failed; draft retained.") : outcome, token.NoteId, pending);
    }
    internal NoteSaveResult SaveModalNote(InlineEditSession session, NoteEditFields fields)
    {
        if (!InlineUiUnlocked) return new(Outcome.Fail(ErrorCode.Conflict, "Vault locked."), session.EffectiveNoteId, false);
        if (session.Token is { } token) {
            var actual = app.State.Notes.SingleOrDefault(n => n.Id == token.NoteId);
            if (!ArchivePending && !pendingEditFields.ContainsKey(token.NoteId) && actual is not null &&
                NoteEditFields.From(actual).Normalized == token.Original.Normalized && fields.Normalized == token.Original.Normalized)
                return new(Outcome.Ok(), token.NoteId, false);
            return SaveEdit(token, fields, session);
        }
        if (session.EffectiveNoteId is Guid bound) {
            if (ArchivePending) return new(Outcome.Fail(ErrorCode.Conflict, "Complete the pending operation in History or Trash, then retry Save."), bound, true);
            // A bound ID without verified content is never permission to create again.
            return new(Outcome.Fail(ErrorCode.Conflict, "Bound note is missing or unverified. Review recovery before saving."), bound, false);
        }
        if (!ArchiveReady()) return new(Outcome.Fail(ErrorCode.Conflict, "Storage unavailable; draft retained."), null, ArchivePending);
        var persisted = DraftCoordinator.Flush(session);
        if (!persisted.Success) return new(persisted, null, false);
        var beforeIds = app.State.Notes.Select(n => n.Id).ToHashSet();
        Outcome outcome;
        try {
            outcome = archiveMutations.Execute(new PutNote(null, fields.NotebookId, fields.Title, fields.Body, fields.Flair), app.State.Revision, archiveMutations.LibraryState.Revision, candidate => {
                var added = candidate.Notes.Where(n => !beforeIds.Contains(n.Id)).ToArray();
                if (added.Length != 1 || NoteEditFields.From(added[0]).Normalized != fields.Normalized)
                    return Outcome.Fail(ErrorCode.Conflict, "New-note candidate identity is ambiguous.");
                return DraftCoordinator.BindIdentity(session, added[0].Id);
            });
        } catch (Exception ex) when (ArchiveError(ex)) { outcome = Outcome.Fail(ErrorCode.StorageFailure, ex.Message); }
        bool success = CompleteArchiveMutation(outcome);
        return new(success ? Outcome.Ok() : outcome.Success ? Outcome.Fail(ErrorCode.StorageFailure, "Reload failed; draft retained.") : outcome, session.EffectiveNoteId, ArchivePending);
    }
    internal NoteSaveResult VerifyModalSubmission(InlineEditSession session, NoteEditFields submitted)
    {
        var id = session.EffectiveNoteId;
        if (!InlineUiUnlocked || ArchivePending) return new(Outcome.Fail(ErrorCode.Conflict, "Complete the pending operation in History or Trash, then retry Save."), id, true);
        var note = app.State.Notes.SingleOrDefault(n => n.Id == id);
        if (note is null || NoteEditFields.From(note).Normalized != submitted.Normalized)
            return new(Outcome.Fail(ErrorCode.Conflict, "Bound note is missing or changed; draft retained."), id, false);
        session.MarkSaved(note.Id, new(note.Id, submitted.Normalized));
        return new(Outcome.Ok(), note.Id, false);
    }
    private bool BeginInlineEdit(Guid noteId)
    {
        if (!InlineUiUnlocked || !ArchiveReady()) return false;
        if (inlineSession?.Token?.NoteId == noteId) { inlineSession.Surface.FocusSource(); return true; }
        if (!MayLeave() || !ReviewRecovery()) return false;
        var token = BeginEdit(noteId);
        var surface = new NoteEditingSurface(app.State.Notebooks, token.Original);
        AttachWikiSurface(surface, () => InlineUiUnlocked && ReferenceEquals(inlineSession?.Surface, surface));
        var session = new InlineEditSession(Guid.NewGuid(), token, app.State.Revision, surface, false);
        var acquired = DraftCoordinator.TryAcquire(session);
        if (!acquired.Success) { SetStatus(acquired.Error!.Message, true); session.Dispose(); return false; }
        inlineSession = session;
        surface.SaveRequested += InlineSaveRequested; surface.CancelRequested += InlineCancelRequested;
        surface.NewRequested += InlineNewRequested; surface.ExportRequested += InlineExportRequested;
        var footer = new WrapPanel(); footer.Children.Add(Btn("Save note", () => SaveInline(), true)); footer.Children.Add(Btn("Cancel editing", () => { if (TryLeave(LeaveReason.CancelEdit)) RefreshDetail(); }));
        DockPanel.SetDock(footer, Dock.Bottom); inlineHost.Children.Add(footer);
        inlineHost.Children.Add(new ScrollViewer { Content = surface, MaxHeight = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        inlineHost.Visibility = Visibility.Visible; RefreshDetail(); surface.FocusSource(); return true;
    }
    private void InlineSaveRequested(object? sender, EventArgs e) => SaveInline();
    private void InlineCancelRequested(object? sender, EventArgs e) { if (TryLeave(LeaveReason.CancelEdit)) RefreshDetail(); }
    private void InlineNewRequested(object? sender, EventArgs e) { if (InlineUiUnlocked) NewNote(); }
    private void InlineExportRequested(object? sender, EventArgs e) { if (InlineUiUnlocked) ExportInlineDraft(); }
    private void ExportInlineDraft()
    {
        if (inlineSession is null || !InlineUiUnlocked) return;
        var ticket = CapturePrivateExportTicket(); var fields = inlineSession.Surface.Fields;
        if (!ConfirmPrivatePlaintextExport()) return;
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = "Markdown|*.md", FileName = "Threadbook-draft.md", OverwritePrompt = true };
        if (picker.ShowDialog(this) != true) return;
        try { CompletePrivateExport(ticket, () => ExportSafety.Write(picker.FileName, dataDir, fields.Body)); }
        catch (Exception ex) when (ArchiveError(ex)) { SetStatus("Draft export failed: " + ex.Message, true); }
    }
    private bool SaveInline()
    {
        if (inlineSession is not { } session || !InlineUiUnlocked) return false;
        var fields = session.Surface.Fields;
        var result = session.IsDirty ? SaveEdit(session.Token!, fields, session) : new NoteSaveResult(Outcome.Ok(), session.Token!.NoteId, false);
        if (!result.Outcome.Success) { session.Surface.Error.Text = result.Outcome.Error!.Message; session.MarkConflict(); return false; }
        session.MarkSaved(session.Token!.NoteId, new(session.Token.NoteId, fields.Normalized));
        var cleanup = DraftCoordinator.Complete(session);
        if (!cleanup.Success) { session.Surface.Error.Text = cleanup.Error!.Message; return false; }
        EndInline(); Refresh(); return true;
    }
    private void EndInline()
    {
        if (inlineSession is not { } session) return;
        session.Surface.SaveRequested -= InlineSaveRequested; session.Surface.CancelRequested -= InlineCancelRequested;
        session.Surface.NewRequested -= InlineNewRequested; session.Surface.ExportRequested -= InlineExportRequested;
        inlineHost.Children.Clear(); inlineHost.Visibility = Visibility.Collapsed; DraftCoordinator.Release(session); session.Dispose(); inlineSession = null;
    }
    private bool TryLeave(LeaveReason reason)
    {
        if (!InlineUiUnlocked) return false;
        var session = inlineSession;
        var decision = MessageBoxResult.No;
        if (session?.IsDirty == true) {
            decision = confirmNoteLeaveForTest?.Invoke() ?? MessageBox.Show(this, "Save changes to this note before leaving?", "Unsaved note", MessageBoxButton.YesNoCancel);
            if (decision == MessageBoxResult.Cancel) return false;
        }
        // Gather both decisions before applying any save, discard or navigation.
        var replyDecision = GatherReplyDecision();
        if (replyDecision == MessageBoxResult.Cancel) return false;
        if (decision == MessageBoxResult.Yes && !SaveInline()) return false;
        if (replyDecision == MessageBoxResult.Yes && !SaveReplyForLeave()) return false;
        if (session is not null) {
            if (decision != MessageBoxResult.Yes) { var result = DraftCoordinator.Discard(session); if (!result.Success) { SetStatus(result.Error!.Message, true); return false; } EndInline(); }
        }
        if (!string.IsNullOrWhiteSpace(replyBox.Text)) ClearReply();
        return true;
    }
    private MessageBoxResult GatherReplyDecision()
    {
        if (string.IsNullOrWhiteSpace(replyBox.Text)) return MessageBoxResult.No;
        if (confirmLeaveForTest is not null) return confirmLeaveForTest() ? MessageBoxResult.No : MessageBoxResult.Cancel;
        return MessageBox.Show(this, "Save your reply before leaving? Yes: Save. No: Discard. Cancel: keep editing.", "Unsaved reply", MessageBoxButton.YesNoCancel);
    }
    private bool SaveReplyForLeave() => InlineUiUnlocked && selectedId is Guid id && Run(new PutReply(editingReplyId, id, parentReplyId, replyBox.Text));
    private bool MayLeaveReply()
    {
        if (!InlineUiUnlocked) return false;
        var decision = GatherReplyDecision();
        if (decision == MessageBoxResult.Cancel || decision == MessageBoxResult.Yes && !SaveReplyForLeave()) return false;
        ClearReply(); return true;
    }
}
