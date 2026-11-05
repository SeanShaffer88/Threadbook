using Threadbook.Core;

namespace Threadbook.Desktop;

public sealed partial class MainWindow
{
    // All context changes happen after the leave decision; future editors extend MayLeave.
    private bool TryNavigate(Guid? book, Guid? note, bool saved = false, bool reveal = false)
    {
        if (vaultLocked || !store.Files.IsUnlocked) return false;
        var target = note is Guid id ? app.State.Notes.FirstOrDefault(n => n.Id == id) : null;
        if (note is not null && target is null) return false;
        if (target is not null) book = target.NotebookId;
        if (book is Guid b && !app.State.Notebooks.Any(n => n.Id == b)) return false;
        bool sameNote = note is not null && selectedId == note;
        bool changesScope = reveal && (search.Text.Length > 0 || libraryFilters != new SearchRequest() || overdueOnly || savedOnly || notebookId != book);
        if ((!sameNote || changesScope) && !MayLeave()) return false;
        if (!sameNote) ClearReply();
        if (reveal) { searchDelay.Stop(); SetSearch(""); searchDelay.Stop(); libraryFilters = new(); overdueOnly = false; }
        if (note is not null && !reveal) { book = notebookId; saved = savedOnly; }
        else feedLimit = 75;
        if (!sameNote || note is null || reveal) replyLimit = 100;
        notebookId = book; savedOnly = saved;
        selectedId = note; showDetail = note is not null;
        if (layoutState.ActiveWorkspaceId is Guid active) {
            layoutState = layoutState with { Workspaces = layoutState.Workspaces.Select(t => t.Id != active ? t : t with {
                NotebookId = book, NoteId = t.NoteId is not null ? note : null,
                SelectedNoteId = note, SavedOnly = saved, Search = search.Text
            }).ToArray() };
        }
        Refresh(); Responsive(); return true;
    }
}
