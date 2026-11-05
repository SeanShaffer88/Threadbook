using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Threadbook.Core;
using Threadbook.Storage;
using Threadbook.Desktop;
static partial class Program
{
    static void PreviewRefreshGates(string root) {
        foreach(bool modalHost in new[]{false,true}) {
            string host=modalHost?"modal":"inline";
            using var store=new JsonStore(Path.Combine(root,"preview-"+host));
            var app=new NotebookService(store); var book=app.State.Notebooks[0].Id;
            app.Execute(new PutNote(null,book,"Target","original","")); var target=app.State.Notes.Last();
            app.Execute(new PutNote(null,book,"Source",WikiLinks.FormatStable(target.Id,"Target"),"")); var source=app.State.Notes.Last();
            var w=new MainWindow(app,store,store.Files.RootDirectory,false); w.Show();
            Window? modal=null;
            try {
                Call(w,"TryNavigate",null,source.Id,false,true); Ready(w);
                object surface;
                if(modalHost) {
                    var type=typeof(MainWindow).Assembly.GetType("Threadbook.Desktop.NoteEditor")!;
                    modal=(Window)Activator.CreateInstance(type,w,app.State.Notebooks,source,(Guid?)book,new Func<Command,bool>(_=>false),null,app.State.Revision,store.Files.RootDirectory,null)!;
                    modal.Show(); Pump(); surface=Field(modal,"surface")!;
                } else { Call(w,"BeginInlineEdit",source.Id); Pump(); surface=Field(w,"inlineSession")!.GetType().GetProperty("Surface")!.GetValue(Field(w,"inlineSession"))!; }
                var body=(TextBox)Field(surface,"body")!; body.Text+="\ntyped draft"; body.Select(2,3);
                var fields=surface.GetType().GetProperty("Fields")!.GetValue(surface); bool undo=body.CanUndo;
                var preview=(RichTextBox)Field(surface,"preview")!;
                Hyperlink Current() => Content(preview.Document.Blocks).OfType<Hyperlink>().Single();
                Click((DependencyObject)surface,"Preview"); var old=Current();
                int prompts=0;
                Set(w,"confirmNoteLeaveForTest",new Func<MessageBoxResult>(()=>{prompts++; return MessageBoxResult.Cancel;}));
                foreach(bool replyChange in new[]{false,true}) {
                    Check(app.Execute(replyChange ? new PutReply(null,target.Id,null,"canonical reply") : new PutNote(target.Id,book,target.Title,"canonical body update","")).Success,host+" canonical mutation");
                    Call(w,"RequestWikiIndex"); Ready(w);
                    Link(old);
                    Check((Guid?)Field(w,"selectedId")==source.Id && prompts==0 && Equals(surface.GetType().GetProperty("Fields")!.GetValue(surface),fields),host+" old document inert after publication");
                    Click((DependencyObject)surface,"Preview"); var current=Current();
                    Check(preview.IsDocumentEnabled && body.SelectionStart==2 && body.SelectionLength==3 && body.CanUndo==undo && Equals(surface.GetType().GetProperty("Fields")!.GetValue(surface),fields),host+" same-surface refresh preserves fields caret undo");
                    if(!modalHost) {
                        Link(current); Check(prompts==1 && (Guid?)Field(w,"selectedId")==source.Id,host+" refreshed actual link reaches cancellable TryLeave");
                        prompts=0;
                    } else {
                        Link(current); Check((Guid?)Field(w,"selectedId")==target.Id,host+" refreshed actual link navigates");
                        Call(w,"TryNavigate",null,source.Id,false,true); Ready(w);
                    }
                    old=current;
                }
                // Superseded document must be inert even when the canonical snapshot stays identical.
                Click((DependencyObject)surface,"Preview"); Link(old);
                Check(prompts==0 && (Guid?)Field(w,"selectedId")==source.Id,host+" old generation rejected without canonical mutation");
                Set(w,"confirmNoteLeaveForTest",new Func<MessageBoxResult>(()=>MessageBoxResult.No));
                Link(Current()); Check((Guid?)Field(w,"selectedId")==target.Id,host+" current generation navigates after discard");
                Check(modalHost ? Equals(surface.GetType().GetProperty("Fields")!.GetValue(surface),fields) : Field(w,"inlineSession") is null,host+" navigation preserves modal draft or completes inline discard");                if(!modalHost) {
                    Call(w,"TryNavigate",null,source.Id,false,true); Ready(w); Call(w,"BeginInlineEdit",source.Id); Pump();
                    var savedSession=Field(w,"inlineSession")!;
                    var savedSurface=savedSession.GetType().GetProperty("Surface")!.GetValue(savedSession)!;
                    var savedBody=(TextBox)Field(savedSurface,"body")!; savedBody.Text+="\nsaved via leave";
                    Click((DependencyObject)savedSurface,"Preview");
                    var savedPreview=(RichTextBox)Field(savedSurface,"preview")!;
                    Set(w,"confirmNoteLeaveForTest",new Func<MessageBoxResult>(()=>MessageBoxResult.Yes));
                    Link(Content(savedPreview.Document.Blocks).OfType<Hyperlink>().Single());
                    Check(app.State.Notes.Single(n=>n.Id==source.Id).Body.EndsWith("saved via leave") && Field(w,"inlineSession") is null,"current preview Save commits canonical draft");
                    Check((Guid?)Field(w,"selectedId")==source.Id,"post-TryLeave snapshot guard rejects navigation after Save changes snapshot");
                    Ready(w); Link(Content(((RichTextBox)Field(w,"wikiReader")!).Document.Blocks).OfType<Hyperlink>().Single());
                    Check((Guid?)Field(w,"selectedId")==target.Id,"new current document navigates after Save publication");
                }
            } finally {
                Set(w,"confirmNoteLeaveForTest",new Func<MessageBoxResult>(()=>MessageBoxResult.No));
                modal?.Close(); w.Close();
            }
        }
    }
}

