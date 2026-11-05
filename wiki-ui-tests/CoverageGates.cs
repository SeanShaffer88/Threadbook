using System.IO;
using System.Windows;
using System.Windows.Controls;
using Threadbook.Core;
using Threadbook.Storage;
using Threadbook.Desktop;
static partial class Program
{
    static void Complete(Task task) {
        var until=DateTime.UtcNow.AddSeconds(8);
        while(!task.IsCompleted && DateTime.UtcNow<until) { Pump(); Thread.Sleep(1); }
        if(!task.IsCompleted) throw new Exception("owned build task timeout");
        task.GetAwaiter().GetResult(); Pump();
    }
    static void CoverageGates(string root) {
        using(var store=JsonStore.CreateEncrypted(Path.Combine(root,"coverage"),"coverage-password".AsSpan())) {
            var app=new NotebookService(store); var book=app.State.Notebooks[0].Id;
            app.Execute(new PutNote(null,book,"Target","","")); var target=app.State.Notes.Last();
            app.Execute(new PutNote(null,book,"Source current",WikiLinks.FormatStable(target.Id,"Target"),"")); var source=app.State.Notes.Last();
            app.Execute(new PutNote(null,book,"Twin","","")); app.Execute(new PutNote(null,book,"Twin","",""));
            var w=new MainWindow(app,store,store.Files.RootDirectory,false); w.Show();
            try {
                Set(w,"selectedId",target.Id); Call(w,"Refresh"); Ready(w);
                var back=(StackPanel)Field(w,"wikiBacklinks")!;
                Check(back.Children.OfType<Button>().Any(b=>((string)b.Content).Contains(source.Title)),"gateway fixture source backlink rendered");
                Check((bool)Call(w,"Run",new DeleteNote(source.Id))!,"Main canonical gateway deletes SOURCE"); Call(w,"Refresh"); Ready(w);
                Check(!back.Children.OfType<Button>().Any(),"source deletion removes actual rendered backlink");
                var entry=new ArchiveStore(store).List().Single(e=>e.NoteIds.Contains(source.Id));
                Check((bool)Call(w,"RestoreArchiveEntry",entry.Id,null)!,"Main Trash gateway restores source"); Call(w,"Refresh"); Ready(w);
                Check(app.State.Notes.Single(n=>n.Id==source.Id).Title==source.Title && back.Children.OfType<Button>().Any(b=>((string)b.Content).Contains(source.Title)),"same restored source ID and current rendered title");
                foreach(bool remove in new[]{false,true}) {
                    Call(w,"TryNavigate",null,source.Id,false,true); Ready(w); Call(w,"BeginInlineEdit",source.Id); Pump();
                    var session=Field(w,"inlineSession")!; var surface=session.GetType().GetProperty("Surface")!.GetValue(session)!;
                    var body=(TextBox)Field(surface,"body")!; body.Text="retained note draft"; var reply=(TextBox)Field(w,"replyBox")!; reply.Text="retained reply draft";
                    Set(w,"confirmNoteLeaveForTest",new Func<MessageBoxResult>(()=>throw new Exception("stale chooser reached leave confirmation")));
                    Call(w,"ActivateWikiLink",book,new WikiLinkToken(0,0,null,"Twin","Twin"),app.State); Pump();
                    var chooser=(Window)Field(w,"wikiChooser")!; var list=(ListBox)Field(w,"wikiChooserResults")!; list.SelectedIndex=0;
                    var id=(Guid)list.SelectedItem.GetType().GetProperty("Id")!.GetValue(list.SelectedItem)!;
                    Check((bool)Call(w,"Run",remove ? new DeleteNote(id) : new PutNote(id,book,"No longer Twin","",""))!,"mutate open chooser "+(remove?"missing selected ID":"stale candidate group"));
                    // Refresh only the production index: preserve the open chooser and both editor widgets.
                    Call(w,"RequestWikiIndex"); Ready(w); Click(chooser,"Choose");
                    Check((Guid?)Field(w,"selectedId")==source.Id && ReferenceEquals(session,Field(w,"inlineSession")) && body.Text=="retained note draft" && reply.Text=="retained reply draft" && chooser.IsVisible,"retained real selection rejects "+(remove?"missing target":"replaced group")+" preserving drafts/context");
                    Call(w,"CloseWikiChooser"); Set(w,"confirmNoteLeaveForTest",new Func<MessageBoxResult>(()=>MessageBoxResult.No)); Set(w,"confirmLeaveForTest",new Func<bool>(()=>true));
                    Call(w,"TryLeave",Enum.Parse(typeof(MainWindow).Assembly.GetType("Threadbook.Desktop.LeaveReason")!,"Navigation"));
                    if(!remove) { Check((bool)Call(w,"Run",new PutNote(id,book,"Twin","",""))!,"restore ambiguous fixture title"); Call(w,"Refresh"); Ready(w); }
                }
                Call(w,"TryNavigate",null,target.Id,false,true); Ready(w);
                var pending=new List<(Snapshot Snapshot,TaskCompletionSource<WikiLinkIndex> Gate,Task Task)>();
                Set(w,"wikiBuildFactory",new Func<Snapshot,WikiLinkIndex?,CancellationToken,Task<WikiLinkIndex>>((snap,previous,token)=> {
                    var gate=new TaskCompletionSource<WikiLinkIndex>(TaskCreationOptions.RunContinuationsAsynchronously); pending.Add((snap,gate,Task.CompletedTask)); return gate.Task;
                }));
                void Request() { Set(w,"wikiRequestedSnapshot",null); Call(w,"RequestWikiIndex"); var i=pending.Count-1; pending[i]=(pending[i].Snapshot,pending[i].Gate,(Task)Field(w,"wikiBuildTask")!); }
                try {
                    Request(); var a=pending.Last();
                    Check((bool)Call(w,"Run",new PutNote(source.Id,book,"Source B",source.Body,""))!,"canonical snapshot B mutation"); Request(); var b=pending.Last();
                    var realB=WikiLinkIndex.Build(b.Snapshot); b.Gate.SetResult(realB); Complete(b.Task);
                    a.Gate.SetResult(WikiLinkIndex.Build(a.Snapshot)); Complete(a.Task);
                    Check(ReferenceEquals(Field(w,"wikiIndex"),realB) && ReferenceEquals(Field(w,"wikiSnapshot"),b.Snapshot),"out of order B then A retains actual B index and UI snapshot");
                    Check(((StackPanel)Field(w,"wikiBacklinks")!).Children.OfType<Button>().Any(t=>((string)t.Content).Contains("Source B")),"B publication updates actual backlinks UI with B current source title");
                    Request(); var pre=pending.Last(); var revision=app.State.Revision;
                    var oldEpoch=(long)Field(w,"wikiEpoch")!; var oldGeneration=(long)Field(w,"wikiGeneration")!;
                    Check((bool)Call(w,"TryLockVault")!,"actual encrypted vault locks with pre-lock index task pending");
                    store.Unlock("coverage-password".AsSpan()); Call(w,"ResumePrivateVault");
                    var post=pending.Last(); pending[^1]=(post.Snapshot,post.Gate,(Task)Field(w,"wikiBuildTask")!); post=pending.Last();
                    var realPost=WikiLinkIndex.Build(post.Snapshot); post.Gate.SetResult(realPost); Complete(post.Task);
                    pre.Gate.SetResult(WikiLinkIndex.Build(pre.Snapshot)); Complete(pre.Task);
                    Check(app.State.Revision==revision && pre.Snapshot.Revision==post.Snapshot.Revision && ReferenceEquals(Field(w,"wikiIndex"),realPost),"pre-lock completion rejected after unlock at SAME canonical revision");
                    // Isolate the epoch/generation guards from token cancellation and revision checks.
                    foreach(bool epochOnly in new[]{true,false}) {
                        var replay=(Task)Call(w,"BuildWikiIndex",app.State,null,CancellationToken.None,epochOnly ? (long)Field(w,"wikiGeneration")! : oldGeneration,epochOnly ? oldEpoch : (long)Field(w,"wikiEpoch")!,app.State.Revision)!;
                        var held=pending.Last(); pending[^1]=(held.Snapshot,held.Gate,replay);
                        held.Gate.SetResult(WikiLinkIndex.Build(held.Snapshot)); Complete(replay);
                        Check(ReferenceEquals(Field(w,"wikiIndex"),realPost),epochOnly ? "epoch alone rejects uncancelled same-revision pre-lock publication" : "generation alone rejects uncancelled same-revision stale publication");
                    }
                } finally {
                    Call(w,"SuspendWikiLinks",false);
                    foreach(var p in pending) p.Gate.TrySetCanceled();
                    foreach(var p in pending) Complete(p.Task);
                    Set(w,"wikiBuildFactory",null);
                }
            } finally { Set(w,"confirmNoteLeaveForTest",new Func<MessageBoxResult>(()=>MessageBoxResult.No)); Set(w,"confirmLeaveForTest",new Func<bool>(()=>true)); w.Close(); }
        }
        LargeChooser(root);
    }
    static void LargeChooser(string root) {
        using var store=new JsonStore(Path.Combine(root,"ten-thousand")); var app=new NotebookService(store);
        var w=new MainWindow(app,store,store.Files.RootDirectory,false); w.Show(); Ready(w);
        try {
            var now=DateTimeOffset.UtcNow;
            var books=Enumerable.Range(0,200).Select(i=>new Notebook(Guid.NewGuid(),$"Book {i:D3}",now)).ToArray();
            var notes=Enumerable.Range(0,10000).Select(i=>new Note(Guid.NewGuid(),books[i/50].Id,"Shared title","","",false,now,now)).ToArray();
            File.WriteAllLines(Path.Combine(root,"synthetic-10000-ids.txt"),notes.Select((n,i)=>$"{i} {n.Id:D} {n.NotebookId:D}"));
            Check(app.Execute(new ReplaceAll(new Snapshot(1,app.State.Revision,books,notes,Array.Empty<Reply>()))).Success,"real canonical 10000 member empty-body fixture committed");
            Check(new FileInfo(store.PrimaryPath).Length<=JsonStore.MaxBytes && JsonStore.Read(store.PrimaryPath).Notes.Count==10000,"real 10000 storage fixture below 25MiB");
            Set(w,"selectedId",notes[0].Id); Call(w,"RequestWikiIndex"); Ready(w);
            Call(w,"ActivateWikiLink",Guid.NewGuid(),new WikiLinkToken(0,0,null,"Shared title","Shared title"),app.State); Pump();
            var chooser=(Window)Field(w,"wikiChooser")!; var list=(ListBox)Field(w,"wikiChooserResults")!; var query=(TextBox)Field(w,"wikiChooserQuery")!;
            Guid[] Ids()=>list.Items.Cast<object>().Select(x=>(Guid)x.GetType().GetProperty("Id")!.GetValue(x)!).ToArray();
            var first=Ids(); Check(first.Length==50,"real 10000 chooser renders bounded first page 50");
            Click(chooser,"Next 50"); var second=Ids(); Check(second.Length==50 && !first.Intersect(second).Any(),"real Next page distinct bounded 50");
            Click(chooser,"Previous"); Check(first.SequenceEqual(Ids()),"real Previous returns original page");
            var later=notes.First(n=>!first.Contains(n.Id) && n.NotebookId==books[199].Id);
            query.Text=books[199].Name;
            Check(list.Items.Count==50 && Ids().Contains(later.Id),"search traverses ALL group members to later notebook");
            list.SelectedIndex=Array.IndexOf(Ids(),later.Id); Click(chooser,"Choose");
            Check((Guid?)Field(w,"selectedId")==later.Id && Field(w,"wikiChooser") is null,"actual 10000 chooser selection navigates exact GUID");
        } finally { w.Close(); }
    }
}
