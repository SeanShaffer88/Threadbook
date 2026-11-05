using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Threadbook.Core;
using Threadbook.Storage;
namespace Threadbook.Desktop;
internal sealed class NoteEditor : EditorWindow
{
    private readonly NoteEditingSurface surface;
    private readonly InlineEditSession session;
    private readonly NoteDraftCoordinator? coordinator;
    private readonly bool ownsCoordinator;
    private readonly Func<Command, bool> commit;
    private readonly Func<InlineEditSession, NoteEditFields, NoteSaveResult>? saveNote;
    private NoteEditFields? pendingSubmission;
    internal Guid? EffectiveNoteId => session.EffectiveNoteId;
    private readonly RecoveryStore? recovery;
    private readonly string dataDir;
    private readonly TextBox title, body, flair, find, replacement;
    private readonly ComboBox notebook;
    private bool committed, acquired, privateSuspended;
    private readonly Note? original;
    private readonly bool missingRecoveredOriginal;
    private bool createRecoveredAsNew;
    private NoteEditFields? canonicalSavedFields;
    private Func<MessageBoxResult>? confirmNewForTest = null;
    public NoteEditor(Window owner, IReadOnlyList<Notebook> books, Note? original, Guid? selected, Func<Command, bool> commit,
        RecoveryStore? recovery = null, long revision = 0, string dataDir = "", RecoveryDraft? restored = null)
        : this(owner, books, original, selected, commit, recovery, revision, dataDir, restored, null) { }
    public NoteEditor(Window owner, IReadOnlyList<Notebook> books, Note? original, Guid? selected, Func<Command, bool> commit,
        RecoveryStore? recovery, long revision, string dataDir, RecoveryDraft? restored, Func<InlineEditSession, NoteEditFields, NoteSaveResult>? saveNote) : base(owner, original is null ? "New note" : "Edit note")
    {
        this.original = original; this.commit = commit; this.saveNote = saveNote; this.recovery = recovery; this.dataDir = dataDir;
        missingRecoveredOriginal = restored?.NoteId is not null && original is null;
        Width = 1000; Height = 900;
        surface = new(books, new(restored?.NotebookId ?? selected ?? original?.NotebookId ?? books.FirstOrDefault()?.Id ?? Guid.Empty,
            restored?.Title ?? original?.Title ?? "", restored?.Body ?? original?.Body ?? "", restored?.Flair ?? original?.Flair ?? ""));
        if (owner is MainWindow wikiHost) wikiHost.AttachWikiSurface(surface, () => CanAct);
        title = surface.title; body = surface.body; flair = surface.flair; find = surface.find; replacement = surface.replacement; notebook = surface.notebook;
        session = new(restored?.DraftId ?? Guid.NewGuid(), original is null ? null : new(original.Id, NoteEditFields.From(original)), revision, surface, restored is not null);
        if (restored?.NoteId is Guid restoredId) session.BindIdentity(restoredId);
        if (recovery is not null) {
            coordinator = owner is MainWindow main ? main.DraftCoordinator : new(recovery, () => !privateSuspended);
            ownsCoordinator = owner is not MainWindow;
            var result = coordinator.TryAcquire(session); acquired = result.Success;
            if (!result.Success) { Error.Text = result.Error!.Message; surface.SetInteractionEnabled(false); }
        } else acquired = true;
        Fields.Children.Add(surface); Actions(() => Save());
        var actions = (StackPanel)Fields.Children[^1]; var cancel = (Button)actions.Children[1]; cancel.IsCancel = false; cancel.Content = "Close · keep draft"; cancel.Click += (_, _) => Close();
        actions.Children.Add(Button("Discard draft", () => { if (!CanAct || MessageBox.Show(this, "Discard this draft? Canonical saved content stays unchanged.", "Discard draft", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; var result = coordinator?.Discard(session) ?? Outcome.Ok(); if (!result.Success) { Error.Text = result.Error!.Message; return; } committed = true; Dirty = false; Close(); }));
        if (missingRecoveredOriginal) actions.Children.Add(Button("Create new note from recovered draft", () => { if (!CanAct) return; createRecoveredAsNew = true; session.AuthorizeReplacement(); pendingSubmission = null; Save(); }));
        PinActions();
        session.StateChanged += SessionChanged;
        surface.SaveRequested += SaveRequested; surface.CancelRequested += CancelRequested; surface.NewRequested += NewRequested; surface.ExportRequested += ExportRequested;
        PreviewKeyDown += EditorKeyDown;
        Closed += ClosedEditor;
        Dirty = session.IsDirty;
        Loaded += (_, _) => { title.Focus(); if (CanAct && recovery is not null) { try { var retained = recovery.LoadSession(); if (original is not null && retained?.NoteId == original.Id) body.CaretIndex = Math.Min(retained.Caret, body.Text.Length); } catch (DataException ex) { Error.Text = ex.Message; } } if (restored is not null) Error.Text = "Recovered draft. Review and explicitly Save to commit."; };
    }
    private bool CanAct => acquired && !privateSuspended && (Owner is not MainWindow main || main.InlineUiUnlocked);
    private static Button Button(string text, Action action) { var b = new Button { Content = text }; b.Click += (_, _) => action(); return b; }
    private void SessionChanged(object? sender, EventArgs e) => Dirty = session.IsDirty;
    private void SaveRequested(object? sender, EventArgs e) => Save();
    private void CancelRequested(object? sender, EventArgs e) { if (CanAct) Close(); }
    private void ExportRequested(object? sender, EventArgs e) { if (CanAct) ExportDraft(); }
    private void NewRequested(object? sender, EventArgs e)
    {
        if (!CanAct) return;
        if (session.IsDirty) {
            var choice = confirmNewForTest?.Invoke() ?? MessageBox.Show(this, "Resolve this draft before creating another note. Yes: Save. No: Discard. Cancel: continue editing.", "New note", MessageBoxButton.YesNoCancel);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.Yes) { if (!Save()) return; }
            else { var result = coordinator?.Discard(session) ?? Outcome.Ok(); if (!result.Success) { Error.Text = result.Error!.Message; return; } committed = true; Dirty = false; Close(); }
        } else { var result = coordinator?.Complete(session) ?? Outcome.Ok(); if (!result.Success) { Error.Text = result.Error!.Message; return; } committed = true; Close(); }
        if (Owner is MainWindow main) main.Dispatcher.BeginInvoke(main.RequestNewNote);
    }
    private void EditorKeyDown(object sender, KeyEventArgs e) => e.Handled = HandleShortcut(e.Key, Keyboard.Modifiers);
    internal bool HandleShortcut(Key key, ModifierKeys modifiers) => CanAct && surface.HandleShortcut(key, modifiers);
    internal bool PrepareForVaultLock() { if (!PersistDraft()) return false; privateSuspended = true; session.Suspend(); return true; }
    internal void ResumeAfterVaultUnlock() { privateSuspended = false; session.Resume(); }
    private bool PersistDraft()
    {
        if (!CanAct) return !Dirty;
        var result = coordinator?.Flush(session) ?? Outcome.Ok();
        if (!result.Success) Error.Text = result.Error!.Message;
        return result.Success;
    }
    protected override bool MayClose() { if (privateSuspended || Owner is MainWindow main && !main.InlineUiUnlocked) return true; if (CanAct && recovery is not null && notebook.SelectedValue is Guid id) { var result = recovery.SaveSession(new(1, id, session.EffectiveNoteId, body.CaretIndex, DateTimeOffset.UtcNow)); if (!result.Success) Error.Text = result.Error!.Message; } if (committed || !Dirty) return true; return PersistDraft(); }
    private bool Save()
    {
        if (!CanAct) return false;
        if (missingRecoveredOriginal && !createRecoveredAsNew) { Error.Text = "Original note is missing. Choose Create new note from recovered draft explicitly."; return false; }
        var fields = surface.Fields;
        if (saveNote is not null) return SaveTyped(fields);
        if (canonicalSavedFields is not null && fields != canonicalSavedFields && original is null) { Error.Text = "Saved note awaits recovery cleanup. Resolve cleanup before editing another version."; return false; }
        if (canonicalSavedFields is null && !PersistDraft()) return false;
        bool saved;
        if (canonicalSavedFields == fields) saved = true;
        else saved = commit(new PutNote(original?.Id, fields.NotebookId, fields.Title, fields.Body, fields.Flair));
        if (!saved) { Error.Text = "Could not save. Draft retained. " + Error.Text; return false; }
        canonicalSavedFields = fields;
        if (session.Token is { } token) session.MarkSaved(token.NoteId, new(token.NoteId, fields.Normalized));
        var cleanup = coordinator?.Complete(session) ?? Outcome.Ok();
        if (!cleanup.Success) { Error.Text = "Saved, but recovery cleanup failed: " + cleanup.Error!.Message; return false; }
        committed = true; Dirty = false; Accept(); return true;
    }
    private bool SaveTyped(NoteEditFields fields)
    {
        if (pendingSubmission is not null) {
            // MetadataPending is not proof of canonical commit. Verify exact bound ID first.
            if (Owner is not MainWindow main) { Error.Text = "Pending save requires its owning window."; return false; }
            var verified = main.VerifyModalSubmission(session, pendingSubmission);
            if (!verified.Outcome.Success) { Error.Text = verified.Outcome.Error!.Message; return false; }
            canonicalSavedFields = pendingSubmission; pendingSubmission = null;
        }
        if (canonicalSavedFields?.Normalized != fields.Normalized) {
            if (!PersistDraft()) return false;
            bool creating = session.Token is null;
            var result = saveNote!(session, fields);
            if (!result.Outcome.Success) {
                // Retain intended fields separately from subsequent edits, including note-absent phases.
                if (result.NoteId is not null && (creating || result.MetadataPending)) pendingSubmission = fields.Normalized;
                Error.Text = result.Outcome.Error!.Message; return false;
            }
            if (result.NoteId is not Guid id) { Error.Text = "Saved identity unavailable; draft retained."; return false; }
            canonicalSavedFields = fields.Normalized;
            session.MarkSaved(id, new(id, fields.Normalized));
        }
        var cleanup = coordinator?.Complete(session) ?? Outcome.Ok();
        if (!cleanup.Success) { Error.Text = "Saved, but recovery cleanup failed: " + cleanup.Error!.Message; return false; }
        committed = true; Dirty = false; Accept(); return true;
    }
    private void ClosedEditor(object? sender, EventArgs e)
    {
        session.StateChanged -= SessionChanged;
        surface.SaveRequested -= SaveRequested; surface.CancelRequested -= CancelRequested; surface.NewRequested -= NewRequested; surface.ExportRequested -= ExportRequested;
        PreviewKeyDown -= EditorKeyDown; coordinator?.Release(session); if (ownsCoordinator) coordinator?.Dispose(); session.Dispose();
    }
    private void ExportDraft()
    {
        // Resolve the actual owning vault window, including transitively owned editors.
        Window? owner = Owner;
        while (owner is not null && owner is not MainWindow) owner = owner.Owner;
        if (owner is not MainWindow main) { Error.Text = "Export unavailable without an owning vault window."; return; }
        var ticket = main.CapturePrivateExportTicket();
        if (!main.ConfirmPrivatePlaintextExport()) return;
        var dialog = new SaveFileDialog { Filter = "Markdown|*.md|HTML|*.html", FileName = "Threadbook-draft.md", OverwritePrompt = true };
        if(dialog.ShowDialog(this) != true) return;
        try { if (main.CompletePrivateExport(ticket, () => ExportSafety.Write(dialog.FileName, dataDir, dialog.FilterIndex == 2 ? Markdown.ExportHtml(title.Text, body.Text) : body.Text))) Error.Text = "Exported current unsaved draft. Notebook unchanged."; else Error.Text = "Export cancelled because the vault session changed. Start a new export."; }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or DataException) { Error.Text = "Export failed: " + ex.Message; }
    }

    public bool SaveForTest(string titleValue, string bodyValue, string flairValue) { title.Text = titleValue; body.Text = bodyValue; flair.Text = flairValue; return Save(); }
    public void WritingSmoke(string screenshot) { surface.WritingSmoke(screenshot); if (!PersistDraft()) throw new Exception("Draft recovery"); if (recovery is not null && recovery.LoadDraft()?.Body != body.Text) throw new Exception("Draft roundtrip"); }
}
