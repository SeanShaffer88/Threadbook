using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Threadbook.Core;
using Threadbook.Storage;
namespace Threadbook.Desktop;

public sealed partial class MainWindow
{
    internal LibraryService library = null!;
    private LibraryStore libraryStore = null!;
    private SearchRequest libraryFilters = new();
    private Dictionary<Guid, SearchHit> libraryHits = new();
    private readonly TextBlock libraryWarning = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer reminderTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly HashSet<string> firedReminders = new();
    private bool overdueOnly;
    internal bool LibraryRemindersPaused { get; set; }
    private void InitializeLibrary(DockPanel root)
    {
        libraryStore = new LibraryStore(store); ReloadLibrary();
        archiveWarning.Margin = new Thickness(14, 2, 14, 2); DockPanel.SetDock(archiveWarning, Dock.Top); root.Children.Insert(root.Children.Count - 1, archiveWarning); RefreshArchiveWarning();
        libraryWarning.Margin = new Thickness(14, 2, 14, 2); DockPanel.SetDock(libraryWarning, Dock.Top); root.Children.Insert(root.Children.Count - 1, libraryWarning);
        reminderTimer.Tick += (_, _) => CheckLibraryReminders(DateTimeOffset.UtcNow);
        Loaded += (_, _) => { reminderTimer.Start(); CheckLibraryReminders(DateTimeOffset.UtcNow); };
        Closed += (_, _) => { reminderTimer.Stop(); searchDelay.Stop(); };
    }
    private void ReloadLibrary()
    {
        if (libraryStore is null) return;
        library = new LibraryService(libraryStore, () => app.State);
        libraryWarning.Text = library.Warning ?? "";
    }
    private bool RunLibrary(LibraryCommand command)
    {
        if (!ArchiveReady()) return false;
        var result = library.Execute(command);
        SetStatus(result.Success ? result.Warning ?? "Library saved on this device." : result.Error!.Message, !result.Success);
        if (result.Success) { ReloadLibrary(); Refresh(); }
        return result.Success;
    }
    private IReadOnlyList<Note> QueryLibraryNotes()
    {
        if (library is null) return app.Query(new(notebookId, savedOnly, search.Text, sort));
        try {
            var found = LibrarySearch.Search(app.State, library.State, libraryFilters with { Text = search.Text, NotebookId = notebookId, SavedOnly = savedOnly || libraryFilters.SavedOnly });
            libraryHits = found.Hits.ToDictionary(x => x.NoteId);
            var ids = libraryHits.Keys.ToHashSet();
            var notes = app.Query(new(notebookId, savedOnly, "", sort)).Where(n => ids.Contains(n.Id));
            if (overdueOnly) { var due = DueTasks(DateTimeOffset.UtcNow).Select(t => t.NoteId).ToHashSet(); notes = notes.Where(n => due.Contains(n.Id)); }
            return notes.ToArray();
        } catch (DataException ex) { SetStatus("Search rejected: " + ex.Message, true); libraryHits.Clear(); return Array.Empty<Note>(); }
    }
    private string LibraryExcerpt(Note note, int limit)
    {
        if (search.Text.Length > 0 && libraryHits.TryGetValue(note.Id, out var hit)) return hit.Source + ": " + hit.Snippet;
        return note.Body.Length > limit ? note.Body[..limit] + "…" : note.Body;
    }
    private string FolderPath(Guid id)
    {
        var parts = new List<string>(); Guid? cursor = id;
        for (int i = 0; cursor is Guid key && i < 8; i++) { var f = library.State.Folders.FirstOrDefault(x => x.Id == key); if (f is null) break; parts.Insert(0, f.Name); cursor = f.ParentId; }
        return string.Join(" / ", parts);
    }
    private IEnumerable<(Guid? Id, string Name)> FolderChoices() => new[] { ((Guid?)null, "No folder (root)") }.Concat(library.State.Folders.Select(f => ((Guid?)f.Id, FolderPath(f.Id))).OrderBy(x => x.Item2));
    private void OpenLibrary()
    {
        if (!ArchiveReady()) return;
        ReloadLibrary(); var d = new LibraryDialog(this, "Library · folders and tags");
        d.Fields.Children.Add(Label("Metadata applies to committed notes. Deleting a folder moves children and notebooks to its parent; deleting a tag removes assignments.", 13));
        if (library.WritesBlocked) d.Action("Preserve damaged metadata and reset…", () => {
            if (MessageBox.Show(d, "Preserve original metadata bytes and reset library metadata? Notes remain unchanged.", "Reset library", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            if (!ArchiveReady()) return; var result = libraryStore.Reset(app.State); if (!result.Success) throw new DataException(result.Error!.Code, result.Error.Message); ReloadLibrary(); Refresh(); d.Close();
        });
        var folders = d.Choice("Folder", FolderChoices());
        d.Action("New folder", () => EditFolder(null));
        d.Action("Edit / move selected folder", () => { if (folders.SelectedValue is Guid id) EditFolder(library.State.Folders.First(f => f.Id == id)); });
        d.Action("Delete selected folder", () => { if (folders.SelectedValue is Guid id && MessageBox.Show(d, "Delete folder and move its children to its parent?", "Delete folder", MessageBoxButton.YesNo) == MessageBoxResult.Yes && RunLibrary(new DeleteLibraryFolder(id))) d.Close(); });
        var books = d.Choice("Notebook to assign", app.State.Notebooks.Select(b => ((Guid?)b.Id, b.Name)), notebookId);
        d.Action("Assign notebook to selected folder", () => { if (books.SelectedValue is Guid id && RunLibrary(new AssignNotebookFolder(id, folders.SelectedValue as Guid?))) d.Close(); });
        var tags = d.Choice("Tag", library.State.Tags.Select(t => ((Guid?)t.Id, t.Name)));
        d.Action("New tag", () => EditTag(null));
        d.Action("Rename selected tag", () => { if (tags.SelectedValue is Guid id) EditTag(library.State.Tags.First(t => t.Id == id)); });
        d.Action("Delete selected tag", () => { if (tags.SelectedValue is Guid id && MessageBox.Show(d, "Delete tag and its assignments?", "Delete tag", MessageBoxButton.YesNo) == MessageBoxResult.Yes && RunLibrary(new DeleteLibraryTag(id))) d.Close(); });
        d.Action("Edit tags on selected committed note", () => { if (selectedId is Guid id) EditNoteTags(id); else SetStatus("Select a committed note first."); });
        d.ShowDialog(); Refresh();
    }
    private void EditFolder(LibraryFolder? folder)
    {
        if (!ArchiveReady()) return;
        var d = new LibraryDialog(this, folder is null ? "New folder" : "Edit folder");
        var name = d.Text("Folder name", folder?.Name ?? "", 60); var parent = d.Choice("Parent folder", FolderChoices().Where(x => x.Id != folder?.Id || x.Id is null), folder?.ParentId);
        d.Save(() => RunLibrary(new PutLibraryFolder(folder?.Id, parent.SelectedValue as Guid?, name.Text))); d.ShowDialog();
    }
    private void EditTag(LibraryTag? tag)
    {
        if (!ArchiveReady()) return;
        var d = new LibraryDialog(this, "Tag"); var name = d.Text("Tag name", tag?.Name ?? "", 60); d.Save(() => RunLibrary(new PutLibraryTag(tag?.Id, name.Text))); d.ShowDialog();
    }
    private List<(Guid Id, CheckBox Box)> TagChecks(LibraryDialog d, IReadOnlyList<Guid> assigned)
    {
        var boxes = new List<(Guid, CheckBox)>();
        foreach (var tag in library.State.Tags.OrderBy(t => t.Name)) { var check = new CheckBox { Content = tag.Name, IsChecked = assigned.Contains(tag.Id), Margin = new Thickness(0,4,0,4) }; d.Fields.Children.Add(check); boxes.Add((tag.Id, check)); }
        return boxes;
    }
    private void EditNoteTags(Guid note)
    {
        if (!ArchiveReady()) return;
        var d = new LibraryDialog(this, "Tags on committed note"); var checks = TagChecks(d, library.State.NoteTags.GetValueOrDefault(note) ?? Array.Empty<Guid>());
        d.Save(() => RunLibrary(new AssignNoteTags(note, checks.Where(x => x.Box.IsChecked == true).Select(x => x.Id).ToArray()))); d.ShowDialog();
    }
    private void OpenTemplates()
    {
        if (!ArchiveReady()) return;
        var d = new LibraryDialog(this, "Note templates");
        var all = LibraryTemplates.BuiltIns.Concat(library.State.Templates).ToArray();
        var picker = d.Choice("Template (built-ins are copied when edited)", all.Select(t => ((Guid?)t.Id, t.Name)));
        var book = d.Choice("Create in notebook", app.State.Notebooks.Select(b => ((Guid?)b.Id, b.Name)), notebookId ?? app.State.Notebooks.FirstOrDefault()?.Id);
        d.Action("Create note from template", () => { if (picker.SelectedValue is Guid id && book.SelectedValue is Guid bid && MayLeave() && CreateTemplateNote(all.First(t => t.Id == id), bid)) d.Close(); });
        d.Action("New custom template", () => EditTemplate(null));
        d.Action("Edit / copy template", () => { if (picker.SelectedValue is Guid id) EditTemplate(all.First(t => t.Id == id)); });
        d.Action("Delete custom template", () => { if (picker.SelectedValue is Guid id && library.State.Templates.Any(t => t.Id == id) && MessageBox.Show(d, "Delete custom template?", "Delete template", MessageBoxButton.YesNo) == MessageBoxResult.Yes && RunLibrary(new DeleteNoteTemplate(id))) d.Close(); }); d.ShowDialog();
    }
    private void EditTemplate(NoteTemplate? template)
    {
        if (!ArchiveReady()) return;
        bool custom = template is not null && library.State.Templates.Any(t => t.Id == template.Id);
        var d = new LibraryDialog(this, "Custom template"); var name = d.Text("Template name", template?.Name + (template is not null && !custom ? " copy" : ""), 60);
        var title = d.Text("Note title", template?.Title ?? "", 200); var body = d.Text("Markdown body", template?.Body ?? "", 100000, true); var flair = d.Text("Flair", template?.Flair ?? "", 40);
        var tags = TagChecks(d, template?.TagIds ?? Array.Empty<Guid>());
        d.Save(() => RunLibrary(new PutNoteTemplate(custom ? template!.Id : null, name.Text, title.Text, body.Text, flair.Text, tags.Where(x => x.Box.IsChecked == true).Select(x => x.Id).ToArray()))); d.ShowDialog();
    }
    private bool CreateTemplateNote(NoteTemplate template, Guid book)
    {
        if (!InlineUiUnlocked || !MayLeave()) return false;
        try {
            if (recovery.LoadDraft() is not null) { SetStatus("Resolve this draft before creating another note. Review recovery first.", true); return false; }
        } catch (DataException ex) { SetStatus(ex.Message, true); return false; }
        if (!Run(new PutNote(null, book, template.Title, template.Body, template.Flair))) return false;
        var note = app.State.Notes.Last(); selectedId = note.Id; notebookId = book; savedOnly = false; SetSearch(""); ClearReply(); showDetail = true;
        var tags = RunLibrary(new AssignNoteTags(note.Id, template.TagIds)); ReloadLibrary(); Refresh(); Responsive();
        SetStatus(tags ? "Template note created." : "Note created, but default tags failed: " + status.Text + ". Apply tags to this existing note; do not create it again.", !tags); return true;
    }
    private void OpenLibraryFilters()
    {
        var d = new LibraryDialog(this, "Search filters · all layouts");
        d.Fields.Children.Add(Label("Notebook and Saved navigation also apply. All selected tags must match. Dates are UTC; end is exclusive.", 13));
        var tags = TagChecks(d, libraryFilters.TagIds ?? Array.Empty<Guid>());
        var from = d.Text("Updated from UTC (yyyy-MM-dd HH:mm)", libraryFilters.UpdatedFromUtc?.ToString("yyyy-MM-dd HH:mm") ?? "");
        var before = d.Text("Updated before UTC (yyyy-MM-dd HH:mm)", libraryFilters.UpdatedBeforeUtc?.ToString("yyyy-MM-dd HH:mm") ?? "");
        var attachments = new CheckBox { Content = "Has attachments", IsChecked = libraryFilters.HasAttachments }; d.Fields.Children.Add(attachments);
        var tasks = new CheckBox { Content = "Has managed tasks", IsChecked = libraryFilters.HasTasks }; d.Fields.Children.Add(tasks);
        var saved = new CheckBox { Content = "Saved only", IsChecked = libraryFilters.SavedOnly }; d.Fields.Children.Add(saved);
        d.Action("Clear filters", () => { if (!MayLeave()) return; libraryFilters = new(); overdueOnly = false; RefreshFeed(); d.Close(); });
        d.Save(() => { var candidate = new SearchRequest(TagIds: tags.Where(x => x.Box.IsChecked == true).Select(x => x.Id).ToArray(), UpdatedFromUtc: LibraryDialog.Utc(from.Text), UpdatedBeforeUtc: LibraryDialog.Utc(before.Text), HasAttachments: attachments.IsChecked == true, HasTasks: tasks.IsChecked == true, SavedOnly: saved.IsChecked == true); LibrarySearch.Search(app.State, library.State, candidate); if (!MayLeave()) return false; libraryFilters = candidate; feedLimit = 75; RefreshFeed(); return true; }); d.ShowDialog();
    }
    private void AddLibraryDetail(Note note)
    {
        if (library is null) return;
        var tags = library.State.NoteTags.GetValueOrDefault(note.Id) ?? Array.Empty<Guid>();
        var chips = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
        var names = library.State.Tags.Where(t => tags.Contains(t.Id)).Select(t => t.Name).ToArray();
        foreach (var name in names) { var chip = Btn(name, () => EditNoteTags(note.Id)); chip.MinHeight = 26; chip.FontSize = 12; chip.Padding = new Thickness(8, 2, 8, 2); chips.Children.Add(chip); }
        if (names.Length == 0) { var add = Btn("+ Add tags", () => EditNoteTags(note.Id)); add.BorderThickness = new Thickness(0); add.Background = System.Windows.Media.Brushes.Transparent; add.FontSize = 12; add.MinHeight = 24; add.Padding = new Thickness(0, 2, 8, 2); chips.Children.Add(add); }
        detail.Children.Add(chips);
        var tasks = library.State.Tasks.Where(t => t.NoteId == note.Id).ToArray();
        if (tasks.Length > 0) detail.Children.Add(Label($"{tasks.Count(t => !t.Completed)} open tasks · {tasks.Count(t => !t.Completed && t.DueUtc <= DateTimeOffset.UtcNow)} due / overdue", 12, false, "Muted"));
    }

    private IEnumerable<LibraryTask> DueTasks(DateTimeOffset now) => library.State.Tasks.Where(t => !t.Completed && t.DueUtc is DateTimeOffset due && due <= now && (t.SnoozedUntilUtc is null || t.SnoozedUntilUtc <= now));
    private void CheckLibraryReminders(DateTimeOffset now)
    {
        if (vaultLocked || library is null || LibraryRemindersPaused || !IsVisible) return;
        var fresh = DueTasks(now).Where(t => firedReminders.Add($"{t.Id}:{t.DueUtc:O}:{t.SnoozedUntilUtc:O}")).ToArray();
        if (fresh.Length > 0) SetStatus($"{fresh.Length} task(s) due / overdue. Open Due / overdue to review. Reminders stop when Threadbook exits.");
    }
    private void OpenTasks(Note note, int offset = 0)
    {
        if (!ArchiveReady()) return;
        var d = new LibraryDialog(this, "Tasks and Markdown checklist");
        d.Fields.Children.Add(Label("Due times use UTC. Reminders run only while this app is open; no OS reminders or startup registration.", 13));
        d.Action("Add managed task", () => EditTask(note.Id, null));
        var all = library.State.Tasks.Where(t => t.NoteId == note.Id).ToArray();
        foreach (var task in all.Skip(offset).Take(100)) {
            d.Fields.Children.Add(Label((task.Completed ? "Completed · " : "Open · ") + task.Title + " · due " + (task.DueUtc?.ToString("u") ?? "none") + (task.SnoozedUntilUtc is null ? "" : " · snoozed to " + task.SnoozedUntilUtc.Value.ToString("u")), 13));
            d.Action("Edit: " + task.Title, () => EditTask(note.Id, task));
            d.Action(task.Completed ? "Reopen task" : "Complete task", () => { if (RunLibrary(new CompleteLibraryTask(task.Id, !task.Completed))) d.Close(); });
            d.Action("Snooze 1 hour", () => { if (RunLibrary(new SnoozeLibraryTask(task.Id, DateTimeOffset.UtcNow.AddHours(1)))) d.Close(); });
            d.Action("Delete task", () => { if (MessageBox.Show(d, "Delete managed task?", "Delete task", MessageBoxButton.YesNo) == MessageBoxResult.Yes && RunLibrary(new DeleteLibraryTask(task.Id))) d.Close(); });
        }
        if (offset > 0) d.Action("Previous 100 tasks", () => { d.Close(); OpenTasks(note, Math.Max(0, offset - 100)); });
        if (all.Length > offset + 100) d.Action("Next 100 tasks", () => { d.Close(); OpenTasks(note, offset + 100); });
        var lines = note.Body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'); int count = 0;
        for (int i = 0; i < lines.Length && count < 100; i++) {
            if (!lines[i].Contains("[ ]") && !lines[i].Contains("[x]") && !lines[i].Contains("[X]")) continue;
            try { Checklist.Toggle(note.Body, i, lines[i]); } catch (DataException) { continue; }
            int index = i; string expected = lines[i]; count++;
            d.Action("Toggle " + expected, () => { if (!ArchiveReady()) return; var current = app.State.Notes.FirstOrDefault(n => n.Id == note.Id) ?? throw new DataException(ErrorCode.NotFound, "Note is no longer available."); var changed = Checklist.Toggle(current.Body, index, expected); if (Run(new PutNote(current.Id, current.NotebookId, current.Title, changed, current.Flair))) { Refresh(); d.Close(); } });
        }
        d.ShowDialog();
    }
    private void EditTask(Guid note, LibraryTask? task)
    {
        if (!ArchiveReady()) return;
        var d = new LibraryDialog(this, "Managed task"); var title = d.Text("Task title", task?.Title ?? "", 200); var due = d.Text("Due UTC (yyyy-MM-dd HH:mm), blank for none", task?.DueUtc?.ToString("yyyy-MM-dd HH:mm") ?? "");
        d.Save(() => RunLibrary(new PutLibraryTask(task?.Id, note, title.Text, LibraryDialog.Utc(due.Text), task?.Completed ?? false, task?.SnoozedUntilUtc))); d.ShowDialog();
    }
    private void ImportText(bool folder)
    {
        if (!ArchiveReady()) return;
        if (!MayLeave()) return;
        if (notebookId is not Guid target) { SetStatus("Select a target notebook before importing files.", true); return; }
        string[] paths;
        if (folder) { var pick = new OpenFolderDialog { Title = "Import Markdown/text folder into selected notebook" }; if (pick.ShowDialog(this) != true) return; paths = new[] { pick.FolderName }; }
        else { var pick = new OpenFileDialog { Filter = "Markdown / text|*.md;*.markdown;*.txt", Multiselect = true }; if (pick.ShowDialog(this) != true) return; paths = pick.FileNames; }
        try { CommitPreparedImport(NoteImports.PrepareMarkdownImport(app.State, paths, target)); }
        catch (Exception ex) when (ex is DataException or IOException or UnauthorizedAccessException) { SetStatus("Import rejected: " + ex.Message, true); }
    }
    private void CommitPreparedImport(Snapshot prepared)
    {
        if (!ArchiveReady() || !TryLeave(LeaveReason.NewNote)) return;
        int added = prepared.Notes.Count - app.State.Notes.Count;
        if (MessageBox.Show(this, $"Append {added} notes? Existing notes are preserved. Local image paths remain literal.", "Import preview", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        if (Run(new ReplaceAll(prepared))) { ClearReply(); selectedId = null; Refresh(); }
    }
}
