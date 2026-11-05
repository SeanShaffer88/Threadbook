using System.IO;
using System.Windows;
using System.Windows.Controls;
using Threadbook.Core;
using Threadbook.Storage;
namespace Threadbook.Desktop;
public sealed partial class MainWindow
{
    public async System.Threading.Tasks.Task RunLibrarySmokeTest()
    {
        var evidence = new List<string>();
        var evidenceDir = Path.Combine(Path.GetDirectoryName(dataDir)!, "library-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidenceDir);
        void Check(bool condition, string name) { if (!condition) throw new Exception("Library smoke: " + name + "; status=" + status.Text); evidence.Add("PASS " + name); }
        var book = app.State.Notebooks.First();
        Check(Run(new PutNote(null, book.Id, "Library fixture", new string('a', 300) + " needle-after-preview\r\n- [ ] Verify\r\n```\r\n- [ ] Literal\r\n```", "")), "canonical fixture save");
        var note = app.State.Notes.Last(); selectedId = note.Id; notebookId = null; search.Clear();
        Check(RunLibrary(new PutLibraryFolder(null, null, "Parent")), "create root folder"); var parent = library.State.Folders.Single();
        Check(RunLibrary(new PutLibraryFolder(null, parent.Id, "Child")), "create nested folder"); var child = library.State.Folders.Last();
        Check(RunLibrary(new AssignNotebookFolder(book.Id, child.Id)), "assign notebook");
        Check(FolderPath(child.Id) == "Parent / Child", "nested folder path");
        Check(RunLibrary(new PutLibraryTag(null, "Focus")), "create tag"); var tag = library.State.Tags.Single();
        Check(RunLibrary(new AssignNoteTags(note.Id, new[] { tag.Id })), "tag committed note");
        Check(RunLibrary(new PutNoteTemplate(null, "Fixture template", "From template", "- [ ] New task", "", new[] { tag.Id })), "custom template save");
        var template = library.State.Templates.Single(); int before = app.State.Notes.Count;
        Check(CreateTemplateNote(template, book.Id), "create template note"); var created = app.State.Notes.Last();
        Check(app.State.Notes.Count == before + 1 && library.State.NoteTags[created.Id].Contains(tag.Id), "template canonical save and tags");
        Check(RunLibrary(new PutLibraryTask(null, note.Id, "Due fixture", DateTimeOffset.UtcNow.AddMinutes(-5))), "create overdue task"); var task = library.State.Tasks.Single();
        Check(DueTasks(DateTimeOffset.UtcNow).Any(t => t.Id == task.Id), "overdue query");
        Check(RunLibrary(new SnoozeLibraryTask(task.Id, DateTimeOffset.UtcNow.AddHours(1))), "snooze task");
        Check(!DueTasks(DateTimeOffset.UtcNow).Any(), "snooze suppresses due");
        Check(RunLibrary(new CompleteLibraryTask(task.Id, true)), "complete task");
        Check(RunLibrary(new PutLibraryTask(task.Id, note.Id, "Due edited", DateTimeOffset.UtcNow.AddMinutes(-1))), "edit and reopen task");        LibraryRemindersPaused = true; int fired = firedReminders.Count; CheckLibraryReminders(DateTimeOffset.UtcNow);
        Check(firedReminders.Count == fired, "reminders quiet while private UI paused"); LibraryRemindersPaused = false;
        CheckLibraryReminders(DateTimeOffset.UtcNow); int afterTick = firedReminders.Count; CheckLibraryReminders(DateTimeOffset.UtcNow);
        Check(afterTick == fired + 1 && firedReminders.Count == afterTick, "due reminder fires once per due/snooze version");
        libraryFilters = new(TagIds: new[] { tag.Id }, HasTasks: true); notebookId = null; search.Text = "needle-after-preview";
        foreach (var layout in Enum.GetValues<NotebookLayout>()) {
            ChangeLayout(layout); RefreshFeed(); UpdateLayout();
            Check(QueryLibraryNotes().Select(n => n.Id).SequenceEqual(new[] { note.Id }), "shared full-body tag/task search " + layout);
            Check(LibraryExcerpt(note, 150).Contains("needle-after-preview"), "snippet " + layout);
        }
        selectedId = note.Id; libraryFilters = new(); search.Clear(); overdueOnly = false;
        var changed = Checklist.Toggle(note.Body, 1, "- [ ] Verify");
        Check(changed.Contains("- [x] Verify\r\n") && changed.Contains("- [ ] Literal"), "checklist exact newline/fence preservation");
        Check(Run(new PutNote(note.Id, note.NotebookId, note.Title, changed, note.Flair)), "canonical checklist commit");
        try { Checklist.Toggle(changed, 1, "- [ ] Verify"); throw new Exception("stale toggle accepted"); } catch (DataException ex) { Check(ex.Code == ErrorCode.Conflict, "stale checkbox refused"); }
        try { Checklist.Toggle(changed, 3, "- [ ] Literal"); throw new Exception("fenced toggle accepted"); } catch (DataException) { evidence.Add("PASS fenced checkbox refused"); }
        string input = Path.Combine(evidenceDir, "fixture-import.md"); File.WriteAllText(input, "Imported fixture"); before = app.State.Notes.Count;
        var prepared = NoteImports.PrepareMarkdownImport(app.State, new[] { input }, book.Id);
        Check(app.State.Notes.Count == before, "prepare import has no mutation"); Check(Run(new ReplaceAll(prepared)), "single canonical import commit");
        Check(app.State.Notes.Count == before + 1 && app.State.Notes.Any(n => n.Id == note.Id && n.Body == changed), "file import preserves existing note");
        var incoming = app.State; before = app.State.Notes.Count; Check(Run(new ReplaceAll(NoteImports.PrepareJsonAppend(incoming, app.State))), "JSON append commit");
        Check(app.State.Notes.Count == before * 2 && app.State.Notes.Select(n => n.Id).Distinct().Count() == before * 2, "JSON append fresh identities");
        Check(Run(new DeleteNote(note.Id)), "delete canonical note");
        Check(!library.State.NoteTags.ContainsKey(note.Id) && library.State.Tasks.All(t => t.NoteId != note.Id), "reload projects stale metadata references");
        Check(RunLibrary(new DeleteLibraryFolder(parent.Id)), "folder delete reparents children");
        Check(library.State.Folders.Single().ParentId is null, "folder root reparent");
        Check(RunLibrary(new DeleteNoteTemplate(template.Id)), "delete template"); Check(RunLibrary(new DeleteLibraryTag(tag.Id)), "delete tag");
                void CheckDialog(Action open, string name) {
            Exception? failure = null;
            Dispatcher.BeginInvoke(new Action(() => {
                var visible = OwnedWindows.OfType<LibraryDialog>().FirstOrDefault(w => w.IsVisible);
                try {
                    Check(visible is not null, name + " opens real native dialog");
                    visible!.Width = 420; visible.Height = 400; visible.UpdateLayout();
                    Check((WindowChromeHelper.GetFrame(visible)?.BodyContent ?? visible.Content) is ScrollViewer, name + " scrollable at small size");
                    CaptureVisual(visible, Path.Combine(evidenceDir, name + ".png"));
                } catch (Exception ex) { failure = ex; } finally { visible?.Close(); }
            }));
            open(); if (failure is not null) throw failure;
        }
        CheckDialog(OpenLibrary, "library-manager"); CheckDialog(OpenTemplates, "library-templates");
        CheckDialog(OpenLibraryFilters, "library-search-filters");
        CheckDialog(() => OpenTasks(app.State.Notes.First()), "library-tasks");
        var damaged = System.Text.Encoding.UTF8.GetBytes("{invalid-fixture-library");
        Check(store.Files.WriteAtomic("library.json", damaged, LibraryStore.MaxBytes, false).Success, "write explicit damaged fixture");
        ReloadLibrary(); Check(library.WritesBlocked && !string.IsNullOrEmpty(libraryWarning.Text), "corrupt metadata visible and writes blocked");
        Check(!library.Execute(new PutLibraryTag(null, "Blocked")).Success && store.Files.Read("library.json", LibraryStore.MaxBytes).SequenceEqual(damaged), "blocked save preserves damaged bytes");
        before = app.State.Notes.Count;
        Check(!CreateTemplateNote(LibraryTemplates.BuiltIns.First(), book.Id), "damaged metadata blocks canonical template creation");
        Check(app.State.Notes.Count == before, "damaged metadata preserves canonical content without partial template creation");
        Check(libraryStore.Reset(app.State).Success, "explicit reset"); ReloadLibrary();
        Check(!library.WritesBlocked && store.Files.List("library.preserved-").Any(key => store.Files.Read(key, LibraryStore.MaxBytes).SequenceEqual(damaged)), "reset preserves original metadata bytes");
        var dialog = new LibraryDialog(this, "Fixture dialog"); dialog.Text("Keyboard reachable input"); dialog.Save(() => true);
        Check((WindowChromeHelper.GetFrame(dialog)?.BodyContent ?? dialog.Content) is ScrollViewer, "small-monitor dialog scroll host"); dialog.Close();
        ChangeLayout(NotebookLayout.Original); Refresh(); await System.Threading.Tasks.Task.Delay(100); UpdateLayout();
        Capture(Path.Combine(evidenceDir, "library-ui.png"));
        File.WriteAllText(Path.Combine(evidenceDir, "library-evidence.txt"), string.Join("\n", evidence) + "\n");
        Console.WriteLine("Library evidence: " + evidenceDir);
    }
}
