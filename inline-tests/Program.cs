using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Threadbook.Core;
using Threadbook.Storage;
using Threadbook.Desktop;

static class Program
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static object? Field(object target, string name) => target.GetType().GetField(name, Flags)!.GetValue(target);
    static void Set(object target, string name, object? value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);
    static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    static object? Property(object target, string name) => target.GetType().GetProperty(name, Flags)!.GetValue(target);
    static int assertions;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); assertions++; Console.WriteLine("PASS " + name); }
    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    static IEnumerable<DependencyObject> Tree(DependencyObject root) { yield return root; for (int i=0; i<VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Tree(VisualTreeHelper.GetChild(root,i))) yield return child; }
    static object TypedModal(MainWindow window, NotebookService app, JsonStore store, Note? note = null, RecoveryDraft? restored = null)
    {
        var assembly = typeof(MainWindow).Assembly;
        var editor = assembly.GetType("Threadbook.Desktop.NoteEditor")!;
        var delegateType = typeof(Func<,,>).MakeGenericType(assembly.GetType("Threadbook.Desktop.InlineEditSession")!, assembly.GetType("Threadbook.Desktop.NoteEditFields")!, assembly.GetType("Threadbook.Desktop.NoteSaveResult")!);
        var save = typeof(MainWindow).GetMethod("SaveModalNote", Flags)!.CreateDelegate(delegateType, window);
        return Activator.CreateInstance(editor, window, app.State.Notebooks, note, (Guid?)app.State.Notebooks.First().Id,
            new Func<Command, bool>(c => (bool)Call(window, "Run", c)!), new RecoveryStore(store), app.State.Revision, store.Files.RootDirectory, restored, save)!;
    }
    static void SaveDialog(object modal, Action action)
    {
        var window = (Window)modal; Exception? error = null;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => { try { action(); } catch (Exception ex) { error = ex; } finally { if (window.IsVisible) window.Close(); } }));
        window.ShowDialog(); if (error is not null) throw error;
    }
    static void NewModalRegressions(string root)
    {
        using (var store = new JsonStore(Path.Combine(root, "boolean-callback"))) {
            var app = new NotebookService(store); var book = app.State.Notebooks.First().Id;
            app.Execute(new PutNote(null, book, "existing", "body", "")); var note = app.State.Notes.Single();
            var window = new MainWindow(app, store, store.Files.RootDirectory, false); window.Show(); Pump(); int callbacks = 0;
            var editorType = typeof(MainWindow).Assembly.GetType("Threadbook.Desktop.NoteEditor")!;
            var modal = Activator.CreateInstance(editorType, window, app.State.Notebooks, note, (Guid?)book,
                new Func<Command, bool>(command => { callbacks++; return false; }), new RecoveryStore(store), app.State.Revision, store.Files.RootDirectory, null)!;
            ((Window)modal).Show(); Pump(); var revision = app.State.Revision;
            Check(!(bool)Call(modal, "SaveForTest", "failed boolean", "changed", "")! && callbacks == 1 && app.State.Revision == revision, "legacy boolean callback honored even with actual MainWindow owner");
            ((Window)modal).Close(); new RecoveryStore(store).DiscardDraft(); window.Close();
        }
        foreach (var phase in new[] { "stages", "canonical", "library", "finalize", "cleanup" }) {
            var path = Path.Combine(root, "new-modal-" + phase); Guid bound;
            using (var store = new JsonStore(path)) {
                var app = new NotebookService(store); var window = new MainWindow(app, store, path, false); window.Show(); Pump();
                var recovery = new RecoveryStore(store); var gateway = (ArchiveMutationService)Field(window, "archiveMutations")!;
                var modal = TypedModal(window, app, store); FileStream? held = null;
                gateway.BeforePhase = at => {
                    if (phase == "cleanup" && at == "finalize") held = new FileStream(recovery.DraftPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    else if (at == phase) throw new IOException("injected " + phase);
                };
                SaveDialog(modal, () => {
                    Check(!(bool)Call(modal, "SaveForTest", "Same title", "submitted body", "")!, "new modal interrupted " + phase);
                    var draft = recovery.LoadDraft()!; Check(draft.NoteId is not null, "recovery identity precedes journal " + phase);
                    Check((Guid?)Property(modal, "EffectiveNoteId") == draft.NoteId, "modal retains exact bound identity " + phase);
                    if (phase == "canonical") Check(app.State.Notes.Count == 0 && store.Files.Exists("archive-operation.json"), "prepared journal without canonical note");
                    if (phase == "stages") Check(app.State.Notes.Count == 0 && !store.Files.Exists("archive-operation.json"), "identity before preparation");
                    held?.Dispose(); held = null; gateway.BeforePhase = null;
                });
                held?.Dispose(); gateway.BeforePhase = null;
                bound = recovery.LoadDraft()!.NoteId!.Value;
                Check(recovery.LoadSession()!.NoteId == bound, "CloseKeep session uses bound ID " + phase);
                window.Close();
            }
            // Real store disposal and a new MainWindow; one Application owns all fixtures.
            using (var restarted = new JsonStore(path)) {
                var app = new NotebookService(restarted); var window = new MainWindow(app, restarted, path, false); window.Show(); Pump();
                var gateway = (ArchiveMutationService)Field(window, "archiveMutations")!;
                Check((bool)Call(window, "CompleteArchiveMutation", gateway.ReconcilePending())!, "Main explicit reconcile after restart " + phase);
                var recovery = new RecoveryStore(restarted); var draft = recovery.LoadDraft()!;
                var note = app.State.Notes.SingleOrDefault(n => n.Id == bound);
                var modal = TypedModal(window, app, restarted, note, draft);
                SaveDialog(modal, () => {
                    var before = app.State.Revision;
                    var saved = (bool)Call(modal, "Save")!;
                    Check(saved == (phase != "stages"), "reopened bound recovery save " + phase);
                    Check(app.State.Notes.Count == (phase == "stages" ? 0 : 1) && (note is null || app.State.Notes.Single().Id == bound), "restart creates no duplicate " + phase);
                    if (phase == "cleanup") Check(app.State.Revision == before, "cleanup restart adds no history/write");
                });
                recovery.DiscardDraft(); window.Close();
            }
        }
        using (var store = new JsonStore(Path.Combine(root, "binding-write-failure"))) {
            var app = new NotebookService(store); var window = new MainWindow(app, store, store.Files.RootDirectory, false); window.Show(); Pump();
            var recovery = new RecoveryStore(store); var gateway = (ArchiveMutationService)Field(window, "archiveMutations")!;
            var modal = TypedModal(window, app, store); ((Window)modal).Show(); Pump();
            ((TextBox)Field(modal, "title")!).Text = "binding write fail";
            var session = Field(modal, "session")!; var coordinator = Property(window, "DraftCoordinator")!;
            Check(((Outcome)Call(coordinator, "Flush", session)!).Success, "ordinary draft before binding fault");
            var bytes = File.ReadAllBytes(recovery.DraftPath); var revision = app.State.Revision;
            using (var held = new FileStream(recovery.DraftPath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                var result = gateway.Execute(new PutNote(null, app.State.Notebooks.First().Id, "binding write fail", "", ""), revision, gateway.LibraryState.Revision,
                    candidate => (Outcome)Call(coordinator, "BindIdentity", session, candidate.Notes.Single().Id)!);
                Check(!result.Success && Property(session, "EffectiveNoteId") is null, "failed binding publishes no identity");
                Check(bytes.SequenceEqual(File.ReadAllBytes(recovery.DraftPath)) && app.State.Revision == revision && app.State.Notes.Count == 0 && !Directory.GetFiles(store.Files.RootDirectory, "archive-*").Any(), "binding write failure preserves old bytes and has no archive side effects");
            }
            ((Window)modal).Close(); recovery.DiscardDraft(); window.Close();
        }
        using (var store = new JsonStore(Path.Combine(root, "changed-modal"))) {
            var app = new NotebookService(store); var window = new MainWindow(app, store, store.Files.RootDirectory, false); window.Show(); Pump();
            var gateway = (ArchiveMutationService)Field(window, "archiveMutations")!; var recovery = new RecoveryStore(store);
            var modal = TypedModal(window, app, store);
            SaveDialog(modal, () => {
                gateway.BeforePhase = at => { if (at == "library") throw new IOException("library"); };
                Check(!(bool)Call(modal, "SaveForTest", "changed bound", "submitted", "")!, "bound conflict fixture partial save");
                var id = recovery.LoadDraft()!.NoteId!.Value; var journalPath = Path.Combine(store.Files.RootDirectory, "archive-operation.json");
                var journal = File.ReadAllBytes(journalPath); File.WriteAllText(journalPath, "unknown journal");
                Check(!(bool)Call(modal, "Save")! && app.State.Notes.Count == 1, "unknown pending evidence blocks automatic creation");
                File.WriteAllBytes(journalPath, journal); gateway.BeforePhase = null;
                Check((bool)Call(window, "CompleteArchiveMutation", gateway.ReconcilePending())!, "bound conflict reconcile");
                Check((bool)Call(window, "Run", new PutNote(id, app.State.Notebooks.First().Id, "changed bound", "external change", ""))!, "change exact bound note");
                var revision = app.State.Revision;
                Check(!(bool)Call(modal, "Save")! && app.State.Revision == revision && app.State.Notes.Single().Id == id && recovery.LoadDraft()!.NoteId == id, "changed bound content fails closed without creation");
            }); gateway.BeforePhase = null; recovery.DiscardDraft(); window.Close();
        }
        using (var store = new JsonStore(Path.Combine(root, "cleanup-retry-modal"))) {
            var app = new NotebookService(store); var window = new MainWindow(app, store, store.Files.RootDirectory, false); window.Show(); Pump();
            var gateway = (ArchiveMutationService)Field(window, "archiveMutations")!; var recovery = new RecoveryStore(store);
            var modal = TypedModal(window, app, store); FileStream? held = null;
            SaveDialog(modal, () => {
                gateway.BeforePhase = at => { if (at == "finalize") held = new FileStream(recovery.DraftPath, FileMode.Open, FileAccess.Read, FileShare.Read); };
                Check(!(bool)Call(modal, "SaveForTest", "cleanup retry", "submitted", "")!, "typed new modal cleanup failure");
                var id = recovery.LoadDraft()!.NoteId!.Value; var revision = app.State.Revision;
                held!.Dispose(); held = null; gateway.BeforePhase = null;
                Check((bool)Call(modal, "Save")! && app.State.Revision == revision && app.State.Notes.Single().Id == id && gateway.ListHistory(id).Count == 0, "typed new cleanup retry creates no write or history");
            }); held?.Dispose(); gateway.BeforePhase = null; window.Close();
        }
        using (var store = new JsonStore(Path.Combine(root, "newer-modal"))) {
            var app = new NotebookService(store); var window = new MainWindow(app, store, store.Files.RootDirectory, false); window.Show(); Pump();
            var gateway = (ArchiveMutationService)Field(window, "archiveMutations")!; var recovery = new RecoveryStore(store);
            var modal = TypedModal(window, app, store);
            SaveDialog(modal, () => {
                gateway.BeforePhase = at => { if (at == "library") throw new IOException("library"); };
                Check(!(bool)Call(modal, "SaveForTest", "Newer", "first submission", "")!, "new modal partial commit");
                var id = recovery.LoadDraft()!.NoteId!.Value;
                var body = (TextBox)Field(modal, "body")!; body.BeginChange(); body.SelectAll(); body.SelectedText = "newer submission"; body.EndChange(); body.CaretIndex = 4;
                Check(!(bool)Call(modal, "Save")! && app.State.Notes.Count == 1, "pending blocks another submission");
                gateway.BeforePhase = null; Check((bool)Call(window, "CompleteArchiveMutation", gateway.ReconcilePending())!, "reconcile newer modal");
                gateway.BeforePhase = at => { if (at == "archive") throw new IOException("archive"); };
                Check(!(bool)Call(modal, "Save")! && body.CaretIndex == 4 && body.CanUndo, "newer failure preserves caret undo and baseline");
                gateway.BeforePhase = null;
                Check((bool)Call(modal, "Save")! && app.State.Notes.Single().Id == id && app.State.Notes.Single().Body == "newer submission", "newer modal edits same ID");
            }); gateway.BeforePhase = null; window.Close();
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || Directory.Exists(args[0])) throw new ArgumentException("Explicit absolute NEW fixture required.");
            Directory.CreateDirectory(args[0]); var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Exception? dispatcherError = null; application.DispatcherUnhandledException += (_, e) => { dispatcherError = e.Exception; e.Handled = true; };
            using var store = new JsonStore(Path.Combine(args[0], "plain")); var app = new NotebookService(store); var book = app.State.Notebooks.First().Id;
            Check(app.Execute(new PutNote(null, book, "Inline fixture", "# Heading\noriginal body", "Tag")).Success, "seed note"); var note = app.State.Notes.Last();
            var window = new MainWindow(app, store, store.Files.RootDirectory, false); window.Show(); Pump();
            Call(window,"RunLibrary",new PutLibraryTag(null,"Cancellation tag"));
            Set(window, "selectedId", note.Id); Call(window, "Refresh");
            Check((bool)Call(window, "BeginInlineEdit", note.Id)!, "pencil begins inline session"); Pump();
            var session = Field(window,"inlineSession")!; var surface = Property(session,"Surface")!; var body = (TextBox)Field(surface,"body")!;
            body.BeginChange(); body.SelectAll(); body.SelectedText = "# Changed\nretained draft"; body.EndChange(); body.CaretIndex = 5; Pump(); Console.WriteLine($"BEFORE caret={body.CaretIndex} undo={body.CanUndo}"); var oldRevision = app.State.Revision;
            Call(window,"Refresh"); Console.WriteLine($"AFTER caret={body.CaretIndex} undo={body.CanUndo} identity={ReferenceEquals(surface, Property(Field(window,"inlineSession")!,"Surface"))}"); Check(ReferenceEquals(surface, Property(Field(window,"inlineSession")!,"Surface")) && body.CaretIndex == 5 && body.CanUndo,"refresh retains surface caret and undo");
            Set(window,"confirmNoteLeaveForTest", new Func<MessageBoxResult>(() => MessageBoxResult.Yes));
            Set(window,"confirmLeaveForTest", new Func<bool>(() => false)); var reply = (TextBox)Field(window,"replyBox")!; reply.Text = "keep reply";
            var search = (TextBox)Field(window,"search")!; search.Text = "proposed";
            Check(search.Text == "" && reply.Text == "keep reply" && app.State.Revision == oldRevision && ReferenceEquals(session,Field(window,"inlineSession")),"note Save then reply Cancel mutates neither draft nor query");
            // Exercise real entry points with a later reply cancellation: no side effects allowed.
            Call(window,"SavePreferences"); var preferencePath=Path.Combine(store.Files.RootDirectory,"ui-preferences.json"); var preferenceBytes=File.ReadAllBytes(preferencePath);
            var sortBefore=Field(window,"sort"); var filtersBefore=Field(window,"libraryFilters"); var layoutBefore=Field(window,"layoutState");
            void Unchanged(string route) => Check(app.State.Revision==oldRevision && ReferenceEquals(session,Field(window,"inlineSession")) && reply.Text=="keep reply" && Equals(Field(window,"sort"),sortBefore) && Equals(Field(window,"libraryFilters"),filtersBefore) && Equals(Field(window,"layoutState"),layoutBefore) && preferenceBytes.SequenceEqual(File.ReadAllBytes(preferencePath)),route+" cancellation preserves drafts state and preference bytes");
            Call(window,"SetSearch","selected query"); search.Select(2,5); search.SelectedText="replacement";
            Check(search.Text=="selected query" && search.SelectionStart==2 && search.SelectionLength==5,"selected search rollback retains selection"); Call(window,"SetSearch","");
            var commands=(Dictionary<string,Action>)Field(window,"uiCommands")!;
            foreach(var route in new[]{"Due / overdue","New note"}) { commands[route](); Unchanged(route); }
            Tree(window).OfType<Button>().Single(b=>Equals(b.Content,"Sort ↕")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Unchanged("sort");
            Call(window,"Navigate",null,true); Unchanged("saved filter"); Call(window,"Navigate",book,false); Unchanged("notebook filter");
            ((StackPanel)Field(window,"notebooks")!).Children.OfType<StackPanel>().SelectMany(p=>p.Children.OfType<Button>()).Single(b=>((DockPanel)b.Content).Children.OfType<TextBlock>().Any(t=>t.Text=="Cancellation tag")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Unchanged("tag navigation");
            Call(window,"CloseWorkspace",Guid.NewGuid()); Unchanged("inactive workspace close");
            Call(window,"CreateTemplateNote",LibraryTemplates.BuiltIns.First(),book); Unchanged("new template");
            Call(window,"ReviewRecovery"); Unchanged("recovery replacement");
            Call(window,"RestoreArchiveEntry",Guid.NewGuid(),null); Unchanged("history restore");
            Call(window,"OpenPrivateDirectory",store.Files.RootDirectory); Unchanged("vault switch");
            Call(window,"CommitPreparedImport",app.State); Unchanged("prepared import after picker");
            window.Close(); Check(window.IsVisible,"cancelled close retains shell"); Unchanged("main close");
            foreach(bool clear in new[]{true,false}) {
                window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>{
                    var d=application.Windows.Cast<Window>().Single(w=>w!=window && w.Title.StartsWith("Search filters"));
                    if(!clear) { foreach(var t in Tree(d).OfType<TextBox>()) t.Text="2025-01-01 00:00"; foreach(var check in Tree(d).OfType<CheckBox>()) check.IsChecked=true; }
                    Tree(d).OfType<Button>().Single(b=>Equals(b.Content,clear?"Clear filters":"Save")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); d.Close();
                }));
                Call(window,"OpenLibraryFilters"); Unchanged(clear?"clear filters":"date/all filters apply");
            }
            Set(window,"confirmNoteLeaveForTest", new Func<MessageBoxResult>(() => MessageBoxResult.Cancel));
            Call(window,"ChangeLayout", Enum.ToObject(typeof(MainWindow).Assembly.GetType("Threadbook.Desktop.NotebookLayout")!, 2));
            Check(ReferenceEquals(session,Field(window,"inlineSession")) && body.Text.Contains("retained draft"),"layout cancellation retains editor");
            Call(window,"AddWorkspace", true); Check(ReferenceEquals(session,Field(window,"inlineSession")),"workspace cancellation retains editor");
            Check(app.Execute(new ToggleSaved(note.Id)).Success,"external bookmark fixture");
            Set(window,"savedOnly",true); Call(window,"RefreshFeed");
            Call(window,"Run",new ToggleSaved(note.Id)); Call(window,"RefreshFeed");
            Check(Equals(Field(window,"selectedId"),note.Id) && ReferenceEquals(session,Field(window,"inlineSession")) && body.CaretIndex==5 && body.CanUndo,"bookmark eligibility refresh retains selected surface caret and undo");
            Set(window,"savedOnly",false);
            Check((bool)Call(window,"SaveInline")!,"bookmark changes do not conflict"); Check(reply.Text == "keep reply","note Save leaves reply untouched");
            Check(app.State.Notes.Single(n=>n.Id==note.Id).Body.Contains("retained draft"),"inline commits draft content");
            reply.Clear(); Set(window,"confirmLeaveForTest", new Func<bool>(()=>true));
            Check((bool)Call(window,"BeginInlineEdit",note.Id)!,"reopen inline"); session=Field(window,"inlineSession")!; surface=Property(session,"Surface")!; body=(TextBox)Field(surface,"body")!;
            body.Text="conflicting draft"; var current=app.State.Notes.Single(n=>n.Id==note.Id);
            Check(app.Execute(new PutNote(note.Id,book,"Concurrent title",current.Body,current.Flair)).Success,"concurrent target content");
            Check(!(bool)Call(window,"SaveInline")! && body.Text=="conflicting draft" && ReferenceEquals(session,Field(window,"inlineSession")),"content conflict retains fields and session");
            var coordinator=Property(window,"DraftCoordinator")!; var recovery=new RecoveryStore(store);
            Check(((Outcome)Call(coordinator,"Flush",session)!).Success,"coordinator flushes owned draft"); var first=recovery.LoadDraft()!;
            Check(recovery.SaveDraft(first with {DraftId=Guid.NewGuid(),Body="other owner"}).Success,"inject other owner");
            Check(!((Outcome)Call(coordinator,"Flush",session)!).Success && recovery.LoadDraft()!.Body=="other owner","flush refuses different DraftId");
            Check(!((Outcome)Call(coordinator,"Discard",session)!).Success && recovery.LoadDraft()!.Body=="other owner","cleanup refuses different DraftId");
            recovery.SaveDraft(first); Check(((Outcome)Call(coordinator,"Discard",session)!).Success,"matching identity explicit cleanup"); Call(window,"EndInline");
            Check((bool)Call(window,"BeginInlineEdit",note.Id)!,"begin interrupted metadata save");
            session=Field(window,"inlineSession")!; surface=Property(session,"Surface")!; body=(TextBox)Field(surface,"body")!; body.Text="canonical committed metadata pending";
            var gateway=(ArchiveMutationService)Field(window,"archiveMutations")!;
            gateway.BeforePhase=phase=>{if(phase=="library")throw new IOException("synthetic inline metadata failure");};
            Check(!(bool)Call(window,"SaveInline")! && app.State.Notes.Single(n=>n.Id==note.Id).Body==body.Text,"partial save retains fields after canonical commit");
            var partialRevision=app.State.Revision; var partialHistory=gateway.ListHistory(note.Id).Count;
            gateway.BeforePhase=null; Check((bool)Call(window,"CompleteArchiveMutation",gateway.ReconcilePending())!,"explicit reconciliation while editing");
            Check((bool)Call(window,"SaveInline")! && app.State.Revision==partialRevision && gateway.ListHistory(note.Id).Count==partialHistory,"retry completes exactly one mutation and history capture");
            // Reconciled baseline must survive a later write failure with newer local fields.
            Check((bool)Call(window,"BeginInlineEdit",note.Id)!,"begin newer-fields reconciliation");
            session=Field(window,"inlineSession")!; surface=Property(session,"Surface")!; body=(TextBox)Field(surface,"body")!;
            body.Text="first partial body";
            gateway.BeforePhase=phase=>{if(phase=="library")throw new IOException("metadata fault");};
            Check(!(bool)Call(window,"SaveInline")!,"first body commits with metadata failure");
            gateway.BeforePhase=null; Call(window,"CompleteArchiveMutation",gateway.ReconcilePending());
            var reconciledRevision=app.State.Revision; var reconciledHistory=gateway.ListHistory(note.Id).Count;
            body.Text="newer local body";
            gateway.BeforePhase=phase=>{if(phase=="archive")throw new IOException("next write fault");};
            Check(!(bool)Call(window,"SaveInline")! && body.Text=="newer local body","new fields survive failed write after reconciliation");
            Check(app.State.Revision==reconciledRevision && gateway.ListHistory(note.Id).Count==reconciledHistory,"failed newer write creates no version");
            gateway.BeforePhase=null;
            Check((bool)Call(window,"SaveInline")! && app.State.Revision==reconciledRevision+1 && gateway.ListHistory(note.Id).Count==reconciledHistory+1,"retry uses persisted reconciled token exactly once");
            // Deny deletion while allowing reads, after the canonical mutation has succeeded.
            Check((bool)Call(window,"BeginInlineEdit",note.Id)!,"begin cleanup failure");
            session=Field(window,"inlineSession")!; surface=Property(session,"Surface")!; body=(TextBox)Field(surface,"body")!; body.Text="cleanup committed body";
            Call(coordinator,"Flush",session); FileStream? cleanupLock=null;
            gateway.BeforePhase=phase=>{if(phase=="finalize") cleanupLock=new FileStream(recovery.DraftPath,FileMode.Open,FileAccess.Read,FileShare.Read);};
            Check(!(bool)Call(window,"SaveInline")! && ReferenceEquals(session,Field(window,"inlineSession")),"cleanup failure retains actual editor");
            var cleanupRevision=app.State.Revision; var cleanupHistory=gateway.ListHistory(note.Id).Count;
            cleanupLock!.Dispose(); gateway.BeforePhase=null;
            Check((bool)Call(window,"SaveInline")! && app.State.Revision==cleanupRevision && gateway.ListHistory(note.Id).Count==cleanupHistory,"cleanup retry never duplicates canonical write or history");
            // Real modal shell retains its lease and restores identical content as dirty.
            var editorType=typeof(MainWindow).Assembly.GetType("Threadbook.Desktop.NoteEditor")!;
            object Modal(Note? original,RecoveryDraft? restored=null) => Activator.CreateInstance(editorType,window,app.State.Notebooks,original,(Guid?)book,new Func<Command,bool>(c=>(bool)Call(window,"Run",c)!),recovery,app.State.Revision,store.Files.RootDirectory,restored)!;
            var modal=Modal(app.State.Notes.Single(n=>n.Id==note.Id)); ((Window)modal).Show(); Pump();
            var modalBody=(TextBox)Field(modal,"body")!; modalBody.Text="modal kept body"; ((Window)modal).Close();
            var kept=recovery.LoadDraft()!; var keptBytes=File.ReadAllBytes(recovery.DraftPath); var beforeTemplate=app.State.Revision;
            Check(!(bool)Call(window,"CreateTemplateNote",LibraryTemplates.BuiltIns.First(),book)! && app.State.Revision==beforeTemplate && keptBytes.SequenceEqual(File.ReadAllBytes(recovery.DraftPath)),"kept modal blocks template without overwriting recovery");
            var blocked=Modal(null); Check(!((Control)Field(blocked,"surface")!).IsEnabled,"next modal context blocked by kept draft"); ((Window)blocked).Close();
            recovery.DiscardDraft(); var savedNow=app.State.Notes.Single(n=>n.Id==note.Id);
            var identical=kept with {Title=savedNow.Title,Body=savedNow.Body,Flair=savedNow.Flair,NotebookId=savedNow.NotebookId}; recovery.SaveDraft(identical);
            var restoredModal=Modal(savedNow,identical); ((Window)restoredModal).Show(); Pump();
            Check((bool)Property(Field(restoredModal,"session")!,"IsDirty")!,"actual restored identical modal stays dirty"); ((Window)restoredModal).Close(); recovery.DiscardDraft();
            // Unreadable/future slots block acquisition and preserve exact bytes.
            File.WriteAllText(recovery.DraftPath,"{\"SchemaVersion\":999}"); var bytes=File.ReadAllBytes(recovery.DraftPath);
            var surfaceType=surface.GetType(); var fieldsType=typeof(MainWindow).Assembly.GetType("Threadbook.Desktop.NoteEditFields")!;
            var fields=Activator.CreateInstance(fieldsType,book,"new","body","")!; var candidateSurface=Activator.CreateInstance(surfaceType,app.State.Notebooks,fields)!;
            var candidate=Activator.CreateInstance(session.GetType(),Guid.NewGuid(),null,app.State.Revision,candidateSurface,false)!;
            Check(!((Outcome)Call(coordinator,"TryAcquire",candidate)!).Success && File.ReadAllBytes(recovery.DraftPath).SequenceEqual(bytes),"future/corrupt slot preserved and blocks acquisition");
            Call(candidate,"Dispose"); recovery.DiscardDraft();
            // Run cleanup failure/retry through the actual modal Save handler and DialogResult lifecycle.
            var cleanupModal=Modal(app.State.Notes.Single(n=>n.Id==note.Id));
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>{
                ((TextBox)Field(cleanupModal,"body")!).Text="modal canonical cleanup test";
                Call(coordinator,"Flush",Field(cleanupModal,"session")); FileStream? held=null;
                gateway.BeforePhase=phase=>{if(phase=="finalize") held=new FileStream(recovery.DraftPath,FileMode.Open,FileAccess.Read,FileShare.Read);};
                try {
                    Check(!(bool)Call(cleanupModal,"Save")!,"actual modal cleanup failure retains shell"); var mr=app.State.Revision; var mh=gateway.ListHistory(note.Id).Count;
                    held!.Dispose(); held=null; gateway.BeforePhase=null;
                    Check((bool)Call(cleanupModal,"Save")! && app.State.Revision==mr && gateway.ListHistory(note.Id).Count==mh,"actual modal cleanup retry writes no duplicate version");
                } finally { held?.Dispose(); gateway.BeforePhase=null; if(((Window)cleanupModal).IsVisible)((Window)cleanupModal).Close(); }
            }));
            ((Window)cleanupModal).ShowDialog();
            // Actual modal Ctrl+N uses the shared shortcut and its real decision handler.
            var modalOwner=new Window(); modalOwner.Show();
            var ctrlModal=Activator.CreateInstance(editorType,modalOwner,app.State.Notebooks,null,(Guid?)book,new Func<Command,bool>(c=>app.Execute(c).Success),recovery,app.State.Revision,store.Files.RootDirectory,null)!;
            ((Window)ctrlModal).Show(); Pump(); ((TextBox)Field(ctrlModal,"body")!).Text="CtrlN retained";
            Set(ctrlModal,"confirmNewForTest",new Func<MessageBoxResult>(()=>MessageBoxResult.Cancel));
            Check((bool)Call(ctrlModal,"HandleShortcut",Key.N,ModifierKeys.Control)! && ((Window)ctrlModal).IsVisible,"actual modal CtrlN Cancel preserves context");
            Set(ctrlModal,"confirmNewForTest",new Func<MessageBoxResult>(()=>MessageBoxResult.No));
            Call(ctrlModal,"HandleShortcut",Key.N,ModifierKeys.Control);
            Check(!((Window)ctrlModal).IsVisible && recovery.LoadDraft() is null,"actual modal CtrlN Discard resolves lease before next context"); modalOwner.Close();
            // All six real layouts: sticky actions are outside the bounded editor scroller.
            foreach(int layout in Enumerable.Range(0,6)) {
                Call(window,"ChangeLayout",Enum.ToObject(typeof(MainWindow).Assembly.GetType("Threadbook.Desktop.NotebookLayout")!,layout));
                Check((bool)Call(window,"BeginInlineEdit",note.Id)!,"layout inline " + layout);
                window.MinWidth=800; window.MinHeight=600; window.Width=800; window.Height=600; Set(window,"showDetail",true); Call(window,"Responsive"); Pump(); window.UpdateLayout();
                var host=(FrameworkElement)Field(window,"inlineHost")!; var save=Tree(host).OfType<Button>().Single(b=>Equals(b.Content,"Save note")); var point=save.TranslatePoint(new Point(),window);
                Check(save.IsVisible && save.ActualWidth>0 && point.Y>=0 && point.Y+save.ActualHeight<=window.ActualHeight && point.X>=0 && point.X+save.ActualWidth<=window.ActualWidth,"reachable footer layout " + layout);
                Check(window.ActualWidth==800 && window.ActualHeight==600,"actual compact size " + layout);
                var scroll=Tree(host).OfType<ScrollViewer>().First(); scroll.ScrollToEnd(); Pump();
                var editSurface=Property(Field(window,"inlineSession")!,"Surface")!;
                foreach(var fieldName in new[]{"notebook","title","flair","body"}) {
                    var element=(FrameworkElement)Field(editSurface,fieldName)!; element.BringIntoView(); Pump(); var fp=element.TranslatePoint(new Point(),window);
                    Check(element.ActualWidth>0 && fp.X>=0 && fp.X+element.ActualWidth<=window.ActualWidth && fp.Y<window.ActualHeight && fp.Y+element.ActualHeight>0,"compact field reachable horizontally "+fieldName+" "+layout);
                }
                var header=(FrameworkElement)Field(window,"inlineHeader")!;
                Check(header.TranslatePoint(new Point(),window).Y < host.TranslatePoint(new Point(),window).Y,"saved header precedes editor " + layout);
                var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(window); var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using(var output=File.Create(Path.Combine(args[0],"inline-layout-"+layout+".png"))) png.Save(output);
                Call(window,"EndInline");
            }
            while(gateway.ListHistory(note.Id).Count<20) {
                var saved=app.State.Notes.Single(n=>n.Id==note.Id);
                Check((bool)Call(window,"Run",new PutNote(note.Id,book,saved.Title,saved.Body+"\nversion",saved.Flair))!,"fill explicit history capacity");
            }
            Check((bool)Call(window,"BeginInlineEdit",note.Id)!,"begin at history capacity");
            session=Field(window,"inlineSession")!; surface=Property(session,"Surface")!; body=(TextBox)Field(surface,"body")!; body.Text="capacity retained draft";
            Check(!(bool)Call(window,"SaveInline")! && ReferenceEquals(session,Field(window,"inlineSession")) && body.Text=="capacity retained draft","capacity failure retains editor");
            Check(gateway.ListHistory(note.Id).Count==20,"history remains readable at capacity");
            Check(gateway.PermanentlyDeleteArchive(gateway.ListHistory(note.Id).First().Id).Success,"explicit archive cleanup available while editing");
            Check((bool)Call(window,"SaveInline")!,"save retries after explicit capacity cleanup");
            window.Close();
            using(var encrypted=JsonStore.CreateEncrypted(Path.Combine(args[0],"encrypted"),"synthetic-inline-password".AsSpan())) {
                var encryptedApp=new NotebookService(encrypted); var eb=encryptedApp.State.Notebooks.First().Id;
                encryptedApp.Execute(new PutNote(null,eb,"Locked inline","body","")); var en=encryptedApp.State.Notes.Last();
                var lockedWindow=new MainWindow(encryptedApp,encrypted,encrypted.Files.RootDirectory,false); lockedWindow.Show(); Pump(); Set(lockedWindow,"selectedId",en.Id); Call(lockedWindow,"Refresh");
                Check((bool)Call(lockedWindow,"BeginInlineEdit",en.Id)!,"encrypted inline begins"); Pump();
                var es=Field(lockedWindow,"inlineSession")!; var view=Property(es,"Surface")!; var editorBody=(TextBox)Field(view,"body")!; editorBody.BeginChange(); editorBody.SelectAll(); editorBody.SelectedText="encrypted unsaved"; editorBody.EndChange(); editorBody.CaretIndex=4;
                var draftPath=Path.Combine(encrypted.Files.RootDirectory,"recovery-draft.json"); Directory.CreateDirectory(draftPath);
                Check(!(bool)Call(lockedWindow,"TryLockVault")! && encrypted.Files.IsUnlocked && !(bool)Field(lockedWindow,"vaultLocked")!,"failed inline flush prevents key wipe and UI lock"); Directory.Delete(draftPath);
                var ticket=Call(lockedWindow,"CapturePrivateExportTicket")!;
                Check((bool)Call(lockedWindow,"TryLockVault")! && !encrypted.Files.IsUnlocked,"inline flush precedes successful key wipe");
                Check(!(bool)Call(lockedWindow,"SaveInline")!,"locked retained Save refuses callback");
                encrypted.Unlock("synthetic-inline-password".AsSpan());
                var retainedCoordinator=Property(lockedWindow,"DraftCoordinator")!; var retainedError=(TextBlock)Field(view,"Error")!; retainedError.Text="locked sentinel";
                Call(retainedCoordinator,"Tick",null,EventArgs.Empty);
                Check(retainedError.Text=="locked sentinel","retained timer guard precedes locked UI mutation with unlocked provider");
                var detailPanel=(StackPanel)Field(lockedWindow,"detail")!; var retainedChildren=detailPanel.Children.Cast<UIElement>().ToArray();
                Call(lockedWindow,"RefreshFeed");
                Check(detailPanel.Children.Cast<UIElement>().SequenceEqual(retainedChildren),"retained feed refresh refuses locked UI with unlocked provider");
                var lockedText=editorBody.Text;
                Tree((DependencyObject)view).OfType<Button>().Single(b=>Equals(b.Content,"Bold")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(editorBody.Text==lockedText,"retained formatting callback refuses suspended surface");
                Call(lockedWindow,"ResumePrivateVault");
                Check(ReferenceEquals(view,Property(Field(lockedWindow,"inlineSession")!,"Surface")) && editorBody.CaretIndex==4 && editorBody.CanUndo,"unlock retains same surface caret and undo");
                bool exported=false; Check(!(bool)Call(lockedWindow,"CompletePrivateExport",ticket,new Action(()=>exported=true))! && !exported,"stale inline export ticket blocked");
                Check((bool)Call(lockedWindow,"TryLockVault")!,"relock for locked close"); lockedWindow.Close();
                Check(!encrypted.Files.IsUnlocked,"locked close performs no unlock");
            }
            NewModalRegressions(args[0]);
            Check(dispatcherError is null,"dispatcher clean"); application.Shutdown(); Console.WriteLine($"PASS {assertions} assertions"); return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

