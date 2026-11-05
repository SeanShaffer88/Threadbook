using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Threadbook.Core;
using Threadbook.Storage;
using Threadbook.Desktop;
internal static class Program
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread] public static int Main(string[] args)
    {
        try {
            if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || Directory.Exists(args[0])) throw new ArgumentException("Explicit new absolute fixture required.");
            Directory.CreateDirectory(args[0]);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Exception? dispatcherError = null; application.DispatcherUnhandledException += (_, e) => { dispatcherError = e.Exception; e.Handled = true; };
            int count = 0; void Check(bool yes, string label) { if (!yes) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
            object? Call(object target, string name, params object?[] values) => target.GetType().GetMethod(name, Flags)!.Invoke(target, values);
            object? Field(object target, string name) => target.GetType().GetField(name, Flags)!.GetValue(target);
            void Set(object target, string name, object? value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);
            IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject { if (root is T t) yield return t; for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) foreach(var c in Children<T>(VisualTreeHelper.GetChild(root,i))) yield return c; }
            using var store = new JsonStore(Path.Combine(args[0], "plain")); var app = new NotebookService(store); var main = new MainWindow(app, store, store.Files.RootDirectory, false); main.Show();
            bool Run(Command c) => (bool)Call(main, "Run", c)!;
            bool Meta(LibraryCommand c) => (bool)Call(main, "RunLibrary", c)!;
            var gateway = (ArchiveMutationService)Field(main, "archiveMutations")!;
            var book = app.State.Notebooks.First();
            Check(Run(new PutNote(null, book.Id, "Archive fixture", "original body", "Idea")), "gateway create"); var note = app.State.Notes.Last(); Set(main, "selectedId", note.Id);
            Check(Run(new PutReply(null, note.Id, null, "root reply")), "gateway reply"); var reply = app.State.Replies.Last();
            Check(Run(new PutReply(null, note.Id, reply.Id, "nested reply")), "gateway nested reply");
            Check(Meta(new PutLibraryTag(null, "archive-tag")), "tag metadata"); var library = (LibraryService)Field(main, "library")!; var tag = library.State.Tags.Single();
            Check(Meta(new AssignNoteTags(note.Id, new[]{tag.Id})), "tag assignment");
            Check(Meta(new PutLibraryTask(null, note.Id, "restore task", null, false, null)), "task metadata");
            var attachmentLibrary = new LibraryService(new LibraryStore(store), () => app.State); var attachments = new AttachmentStore(store, attachmentLibrary); byte[] bytes = {1,2,3,4,5}; using(var input = new MemoryStream(bytes)) Check(attachments.AddToNote(attachments.Stage(note.Id,input,"fixture.bin","application/octet-stream")).Success,"real attachment fixture"); Call(main,"ReloadLibrary");
            Check(Run(new PutNote(note.Id,book.Id,"Edited title","edited body","Idea")),"gateway edit");
            Check(gateway.ListHistory(note.Id).Count == 1,"edit captures one content version");
            Window history = (Window)Call(main,"BuildArchiveDialog",false)!; history.Show(); history.UpdateLayout();
            var historyList = Children<ListBox>(history).Single(); Check(historyList.Items.Count == 1 && historyList.SelectedIndex == 0,"History selected actual version"); Check(Children<TextBox>(history).Any(t=>t.IsReadOnly && t.Text.Contains("original body")),"History preview captured body");

            typeof(MainWindow).GetMethod("CaptureVisual", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,new object[]{history,Path.Combine(args[0],"archive-history.png")});
            Children<Button>(history).Single(b=>Equals(b.Content,"Restore version")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); history.Close(); Check(app.State.Notes.Single(n=>n.Id==note.Id).Body=="original body","restore real History button"); Check(gateway.ListHistory(note.Id).Count==2,"restore archives current content first");
            Check(Run(new DeleteNote(note.Id)),"gateway deletion"); var trash = gateway.ListTrash().Single(); Check(!app.State.Notes.Any(n=>n.Id==note.Id),"note deleted");
            Window bin = (Window)Call(main,"BuildArchiveDialog",true)!; bin.Show(); bin.UpdateLayout(); Check(Children<TextBox>(bin).Any(t=>t.Text.Contains("2 replies") && t.Text.Contains("1 attachments")),"Trash shows cascade details"); main.Capture(Path.Combine(args[0],"archive-main.png")); typeof(MainWindow).GetMethod("CaptureVisual", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,new object[]{bin,Path.Combine(args[0],"archive-trash.png")}); bin.Close();
            Check((bool)Call(main,"RestoreArchiveEntry",trash.Id,null)!,"restore real trash action");
            library=(LibraryService)Field(main,"library")!; Check(app.State.Replies.Count(r=>r.NoteId==note.Id)==2 && library.State.Tasks.Any(t=>t.NoteId==note.Id) && library.State.NoteTags[note.Id].Contains(tag.Id),"restored replies tasks tags"); using(var stream=new AttachmentStore(store,library).OpenRead(library.State.Attachments.Single().Id,note.Id)){using var data=new MemoryStream();stream.CopyTo(data);Check(data.ToArray().SequenceEqual(bytes),"restored attachment bytes");}
            Check(!(bool)Call(main,"RestoreArchiveEntry",trash.Id,null)!,"duplicate restore rejected");
            int confirms=0; Set(main,"confirmArchiveRemovalForTest",new Func<bool>(()=>{confirms++;return false;})); Check(!(bool)Call(main,"DeleteArchiveEntry",main,trash.Id)! && gateway.ListTrash().Count==1,"permanent remove No retains entry"); Set(main,"confirmArchiveRemovalForTest",new Func<bool>(()=>{confirms++;return true;})); Check((bool)Call(main,"DeleteArchiveEntry",main,trash.Id)! && confirms==2 && gateway.ListTrash().Count==0,"permanent remove asks each attempt");
            Check(Run(new DeleteNotebook(book.Id)),"notebook cascade gateway"); var cascade=gateway.ListTrash().Single(); Check(gateway.ReadArchive(cascade.Id).Data.Notebooks.Any(b=>b.Id==book.Id),"Trash retains notebook"); Check((bool)Call(main,"RestoreArchiveEntry",cascade.Id,null)! && app.State.Notebooks.Any(b=>b.Id==book.Id),"restore notebook cascade");
            var backupTicket = Call(main, "CapturePrivateExportTicket")!;
            var blockedBackup = Path.Combine(args[0], "pending-backup.zip");
            gateway.BeforePhase=phase=>{if(phase=="library")throw new IOException("synthetic interrupted metadata");};
            Check(!Run(new DeleteNote(note.Id)) && store.Files.Exists("archive-operation.json"),"failed multistep retains pending journal");
            var revision=app.State.Revision; var metadata=store.Files.Read("library.json",LibraryStore.MaxBytes); Check(!Meta(new PutLibraryTag(null,"blocked")),"pending blocks metadata mutation"); Check(!(bool)Call(main,"CompletePrivateExport",Call(main,"CapturePrivateExportTicket"),new Action(()=>throw new Exception("export ran")))!,"pending blocks plaintext export"); Call(main,"NewNote"); Call(main,"Import"); Check(!main.OwnedWindows.Cast<Window>().Any(w=>w.IsVisible),"pending blocks new editor and import"); Check(app.State.Revision==revision && metadata.SequenceEqual(store.Files.Read("library.json",LibraryStore.MaxBytes)),"blocked actions preserve raw metadata revision");
            Check(!(bool)Call(main, "CompletePrivateExport", backupTicket, new Action(() => Call(main, "WritePrivateBackup", blockedBackup)))! && !File.Exists(blockedBackup), "backup ticket rejects journal introduced before completion without writes");
            Check(((TextBlock)Field(main,"archiveWarning")!).Text.Contains("pending"),"pending warning visible"); gateway.BeforePhase=null; Check((bool)Call(main,"CompleteArchiveMutation",gateway.ReconcilePending())!,"explicit retry completes metadata"); Check(!store.Files.Exists("archive-operation.json"),"retry removes journal"); Check((bool)Call(main,"RestoreArchiveEntry",gateway.ListTrash().Last().Id,null)!,"pending deletion remains restorable");
            Check(Run(new DeleteNote(note.Id)),"delete for interrupted restore"); var restoreEntry=gateway.ListTrash().First(e=>gateway.ReadArchive(e.Id).Data.Notes.Any(n=>n.Id==note.Id));
            gateway.BeforePhase=phase=>{if(phase=="library")throw new IOException("synthetic restore metadata failure");};
            Check(!(bool)Call(main,"RestoreArchiveEntry",restoreEntry.Id,null)! && app.State.Notes.Any(n=>n.Id==note.Id),"partial restore reloads actual canonical insertion");
            Check(gateway.ListTrash().Any(e=>e.Id==restoreEntry.Id) && !(bool)Call(main,"RestoreArchiveEntry",restoreEntry.Id,null)!,"partial restore remains visible and rejects duplication"); gateway.BeforePhase=null; Check((bool)Call(main,"CompleteArchiveMutation",gateway.ReconcilePending())!,"partial restore explicit completion");
            gateway.BeforePhase=phase=>{if(phase=="canonical")throw new IOException("synthetic prepared failure");}; var preparedRevision=app.State.Revision;
            Check(!Run(new PutNote(null,book.Id,"cancel fixture","prepared only","")),"prepared operation failure");gateway.BeforePhase=null;
            var preparedDialog=(Window)Call(main,"BuildArchiveDialog",true)!;preparedDialog.Show();preparedDialog.UpdateLayout(); Children<Button>(preparedDialog).Single(b=>Equals(b.Content,"Cancel prepared operation")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));preparedDialog.Close();
            Check(!store.Files.Exists("archive-operation.json") && app.State.Revision==preparedRevision,"Cancel prepared actual button preserves canonical");
            var corruptKey="archive-entry-"+Guid.NewGuid().ToString("N")+".json"; var corrupt=System.Text.Encoding.UTF8.GetBytes("{broken-fixture");Check(store.Files.WriteAtomic(corruptKey,corrupt,2048,false).Success,"corrupt archive fixture");
            Check(!Meta(new PutLibraryTag(null,"blocked corrupt")) && store.Files.Read(corruptKey,2048).SequenceEqual(corrupt),"corrupt archive blocks writes and preserves bytes");
            main.Close();
            char[] password="synthetic-archive-password".ToCharArray(); using(var encrypted=JsonStore.CreateEncrypted(Path.Combine(args[0],"encrypted"),password)){
                var secureApp=new NotebookService(encrypted); var secure=new MainWindow(secureApp,encrypted,encrypted.Files.RootDirectory,false);secure.Show(); Check((bool)Call(secure,"Run",new PutNote(null,secureApp.State.Notebooks.First().Id,"Secret","private fixture",""))!,"encrypted gateway create");
                var retained=(Window)Call(secure,"BuildArchiveDialog",true)!; retained.Show(); retained.UpdateLayout(); var retry=Children<Button>(retained).Single(b=>Equals(b.Content,"Retry completion"));
                Check((bool)Call(secure,"TryLockVault")!,"lock owned archive dialog");Check(!retained.IsVisible,"archive dialog hidden on lock"); var before=secureApp.State.Revision; var status=((TextBlock)Field(secure,"status")!).Text;
                retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(!(bool)Call(secure,"Run",new PutNotebook(null,"blocked"))!,"retained canonical callback locked"); Check(Call(secure,"BuildArchiveDialog",true) is null,"locked archive open blocked"); Check(secureApp.State.Revision==before && ((TextBlock)Field(secure,"status")!).Text==status,"locked callbacks preserve status and revision");
                try{encrypted.Unlock("wrong".AsSpan());throw new Exception("wrong password accepted");}catch(DataException){Check(!encrypted.Files.IsUnlocked,"wrong password remains locked");} encrypted.Unlock(password); retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(secureApp.State.Revision==before,"UI lock guard survives externally unlocked store");retained.Close(); secure.Close();
            } Array.Clear(password);
            if(dispatcherError is not null)throw dispatcherError; Console.WriteLine($"PASS {count} archive UI assertions"); application.Shutdown(); return 0;
        } catch(Exception ex){Console.Error.WriteLine(ex);return 2;}
    }
}
