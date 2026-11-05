using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Threadbook.Core;
using Threadbook.Storage;
using Threadbook.Desktop;
static partial class Program
{
    const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static object? Field(object t,string n) => t.GetType().GetField(n,F)!.GetValue(t);
    static void Set(object t,string n,object? v) => t.GetType().GetField(n,F)!.SetValue(t,v);
    static object? Call(object t,string n,params object?[] a) => t.GetType().GetMethod(n,F)!.Invoke(t,a);
    static int checks;
    static void Check(bool v,string n) { if(!v) throw new Exception(n); checks++; Console.WriteLine("PASS "+n); }
    static void Pump() { var f=new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>f.Continue=false)); Dispatcher.PushFrame(f); }
    static void Ready(MainWindow w) { var until=DateTime.UtcNow.AddSeconds(8); do { Pump(); if((bool)w.GetType().GetProperty("WikiCurrent",F)!.GetValue(w)!) return; Thread.Sleep(10); } while(DateTime.UtcNow<until); throw new Exception("index did not publish"); }
    static IEnumerable<Inline> Inlines(InlineCollection c) { foreach(Inline i in c) { yield return i; if(i is Span s) foreach(var x in Inlines(s.Inlines)) yield return x; } }
    static IEnumerable<Inline> Content(IEnumerable<Block> b) { foreach(var x in b) { if(x is Paragraph p) foreach(var i in Inlines(p.Inlines)) yield return i; if(x is Table t) foreach(var i in Content(t.RowGroups.SelectMany(g=>g.Rows).SelectMany(r=>r.Cells).SelectMany(c=>c.Blocks))) yield return i; } }
    static IEnumerable<DependencyObject> Tree(DependencyObject r) { yield return r; for(int i=0;i<VisualTreeHelper.GetChildrenCount(r);i++) foreach(var c in Tree(VisualTreeHelper.GetChild(r,i))) yield return c; }
    static void Click(DependencyObject r,string name) => Tree(r).OfType<Button>().Single(b=>Equals(b.Content,name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static void Link(Hyperlink l) => l.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
    [STAThread] static int Main(string[] args)
    {
        SetErrorMode(3);
        try {
            if(args.Length!=1 || !Path.IsPathFullyQualified(args[0]) || Directory.Exists(args[0])) throw new ArgumentException("NEW absolute fixture required");
            Directory.CreateDirectory(args[0]); var application=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown }; Exception? dispatcherError=null;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
            application.DispatcherUnhandledException+=(_,e)=> { dispatcherError=e.Exception; e.Handled=true; };
            using var store=new JsonStore(Path.Combine(args[0],"plain")); var app=new NotebookService(store); var book=app.State.Notebooks.First().Id;
            app.Execute(new PutNote(null,book,"Roadmap","# Local roadmap\nOffline planning.","")); var target=app.State.Notes.Last();
            app.Execute(new PutNote(null,book,"Connected ideas","# Connected ideas\nA **[[Roadmap]]** and [[missing]].\n\n| Link | Context |\n| --- | --- |\n| [[Roadmap]] | local |\n\n`[[Roadmap]]` [external](https://example.org)\n"+WikiLinks.FormatStable(target.Id,target.Title),"")); var source=app.State.Notes.Last();
            app.Execute(new PutNote(null,book,"Duplicate","one","")); app.Execute(new PutNote(null,book,"Duplicate","two",""));
            var w=new MainWindow(app,store,store.Files.RootDirectory,false); w.Show(); Set(w,"selectedId",source.Id); Call(w,"Refresh"); Ready(w);
            var viewer=(RichTextBox)Field(w,"wikiReader")!; var links=Content(viewer.Document.Blocks).OfType<Hyperlink>().ToArray();
            Check(viewer.IsReadOnly && viewer.IsDocumentEnabled,"actual reader enables document interaction");
            Check(links.Length==4 && links.All(l=>l.NavigateUri is null),"real local links, table support, code and external exclusions");
            Check(links[0].FontWeight==FontWeights.Bold,"styled local link inherits bold");
            Check(new TextRange(viewer.Document.ContentStart,viewer.Document.ContentEnd).Text.Contains("missing note"),"broken state visible");
            foreach(string mode in new[]{"Dark","Light","System"}) { typeof(MainWindow).Assembly.GetType("Threadbook.Desktop.Theme")!.GetMethod("Apply")!.Invoke(null,new object[]{mode}); Pump();
                var fg=((SolidColorBrush)links[0].Foreground).Color; var bg=((SolidColorBrush)application.Resources["Surface"]).Color;
                static double Lum(Color c) { static double L(byte v) { double n=v/255.0; return n<=.04045?n/12.92:Math.Pow((n+.055)/1.055,2.4); } return .2126*L(c.R)+.7152*L(c.G)+.0722*L(c.B); }
                Check((Math.Max(Lum(fg),Lum(bg))+.05)/(Math.Min(Lum(fg),Lum(bg))+.05)>=4.5,"actual link contrast "+mode);
            }
            typeof(MainWindow).Assembly.GetType("Threadbook.Desktop.Theme")!.GetMethod("Apply")!.Invoke(null,new object[]{"Dark"});
            Link(links[0]); Check((Guid?)Field(w,"selectedId")==target.Id,"actual local hyperlink navigates"); Ready(w);
            var back=(StackPanel)Field(w,"wikiBacklinks")!; Check(back.Children.OfType<Button>().Any(b=>((string)b.Content).Contains(source.Title)),"backlink uses current source title");
            Check(app.Execute(new PutNote(source.Id,book,"Renamed source",source.Body,"")).Success,"canonical rename succeeds"); Call(w,"Refresh"); Ready(w);
            Check(back.Children.OfType<Button>().Any(b=>((string)b.Content).Contains("Renamed source")),"rename refreshes backlink title: "+string.Join(";",back.Children.OfType<Button>().Select(b=>b.Content))+" current="+app.State.Notes.Single(n=>n.Id==source.Id).Title+" selected="+Field(w,"selectedId")+" target="+target.Id+" edges="+string.Join(",",((WikiLinkIndex)Field(w,"wikiIndex")!).GetBacklinks(target.Id))+" body="+app.State.Notes.Single(n=>n.Id==source.Id).Body);
            Call(w,"TryNavigate",null,source.Id,false,true); Ready(w); Call(w,"BeginInlineEdit",source.Id); Pump();
            var session=Field(w,"inlineSession")!; var surface=session.GetType().GetProperty("Surface")!.GetValue(session)!; var body=(TextBox)Field(surface,"body")!;
            body.Text="draft selection"; body.Select(6,9); body.IsUndoEnabled=false; body.IsUndoEnabled=true; var before=body.Text;
            Click((DependencyObject)surface,"Insert note link"); Pump(); Check(Field(w,"wikiChooser") is Window,"shared toolbar opens live chooser");
            Click((Window)Field(w,"wikiChooser")!,"Cancel"); Check(body.Text==before && body.SelectionStart==6 && body.SelectionLength==9 && !body.CanUndo,"chooser cancel preserves text selection undo");
            Click((DependencyObject)surface,"Insert note link"); Pump(); var list=(ListBox)Field(w,"wikiChooserResults")!; var query=(TextBox)Field(w,"wikiChooserQuery")!; query.Text="Roadmap"; list.SelectedIndex=0; Click((Window)Field(w,"wikiChooser")!,"Choose");
            Check(body.Text=="draft "+WikiLinks.FormatStable(target.Id,target.Title),"stable syntax inserts at retained selection"); body.Undo(); Check(body.Text==before,"insertion one undo unit");
            Set(w,"confirmNoteLeaveForTest",new Func<MessageBoxResult>(()=>MessageBoxResult.Cancel)); var draft=body.Text; var reply=(TextBox)Field(w,"replyBox")!; reply.Text="reply draft";
            var oldSelection=Field(w,"selectedId"); Call(w,"ActivateWikiLink",book,new WikiLinkToken(0,0,target.Id,null,target.Title),app.State);
            Check(Equals(Field(w,"selectedId"),oldSelection) && body.Text==draft && reply.Text=="reply draft" && ReferenceEquals(Field(w,"inlineSession"),session),"dirty note cancellation preserves both drafts and editor identity");
            var revision=app.State.Revision; Call(w,"RequestWikiIndex"); Ready(w); Check(ReferenceEquals(session,Field(w,"inlineSession")) && body.Text==draft && body.CanRedo,"index refresh preserves editing widget and undo");
            Set(w,"confirmNoteLeaveForTest",new Func<MessageBoxResult>(()=>MessageBoxResult.No)); Set(w,"confirmLeaveForTest",new Func<bool>(()=>true)); Call(w,"TryLeave",Enum.Parse(typeof(MainWindow).Assembly.GetType("Threadbook.Desktop.LeaveReason")!,"Navigation"));
            Call(w,"ActivateWikiLink",book,new WikiLinkToken(0,0,null,"Duplicate","Duplicate"),app.State); Pump();
            Check(((ListBox)Field(w,"wikiChooserResults")!).SelectedIndex==-1,"ambiguous chooser requires explicit selection"); var selected=Field(w,"selectedId"); Click((Window)Field(w,"wikiChooser")!,"Cancel"); Check(Equals(selected,Field(w,"selectedId")) && app.State.Revision==revision,"ambiguity cancel has no canonical mutation");
            var old=app.State; app.Execute(new PutNote(target.Id,book,"Renamed roadmap",target.Body,"")); Call(w,"ActivateWikiLink",book,new WikiLinkToken(0,0,null,"Roadmap","Roadmap"),old);
            Check(Equals(selected,Field(w,"selectedId")),"stale link activation rejected"); Call(w,"Refresh"); Ready(w);
            Call(w,"ActivateWikiLink",book,new WikiLinkToken(0,0,target.Id,null,"Roadmap"),app.State); Check((Guid?)Field(w,"selectedId")==target.Id,"stable ID survives rename"); Ready(w);
            w.UpdateLayout(); typeof(MainWindow).GetMethod("CaptureVisual",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public)!.Invoke(null,new object[]{w,Path.Combine(args[0],"wiki-main.png")});
            Call(w,"ActivateWikiLink",book,new WikiLinkToken(0,0,null,"Duplicate","Duplicate"),app.State); Pump(); var chooser=(Window)Field(w,"wikiChooser")!;
            typeof(MainWindow).GetMethod("CaptureVisual",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public)!.Invoke(null,new object[]{chooser,Path.Combine(args[0],"wiki-chooser.png")}); Call(w,"CloseWikiChooser");
            Set(w,"vaultLocked",true); var status=((TextBlock)Field(w,"status")!).Text; Link(links[0]); Call(w,"ActivateWikiLink",book,new WikiLinkToken(0,0,target.Id,null,"Roadmap"),app.State); Check(((TextBlock)Field(w,"status")!).Text==status && Field(w,"wikiChooser") is null,"retained locked callbacks inert with unlocked provider");
            w.Close();
            var renderer=typeof(MainWindow).Assembly.GetType("Threadbook.Desktop.MarkdownView")!;
            var big=string.Join("\n\n",Enumerable.Repeat(string.Join(" ",Enumerable.Repeat("**[[local]]**",10)),400));
            var bounded=(FlowDocument)renderer.GetMethod("Document")!.Invoke(null,new object?[]{big,400,new Action<WikiLinkToken>(_=>{}),null})!;
            Check(Content(bounded.Blocks).Count()<=3001,"nested link/composite/run span budget bounded");
            var tables=string.Join("\n\n",Enumerable.Repeat("| A | B |\n| --- | --- |\n| [[local]] | value |",400));
            var tableDoc=(FlowDocument)renderer.GetMethod("Document")!.Invoke(null,new object?[]{tables,400,new Action<WikiLinkToken>(_=>{}),null})!;
            Check(tableDoc.Blocks.Count<=400,"table diagnostics cannot bypass block budget");
            Check(tableDoc.Blocks.OfType<Table>().SelectMany(t=>t.RowGroups).SelectMany(g=>g.Rows).SelectMany(r=>r.Cells).Count()<=1000,"whole document cell budget bounded");
            using(var encrypted=JsonStore.CreateEncrypted(Path.Combine(args[0],"encrypted"),"synthetic-wiki-password".AsSpan())) {
                var ea=new NotebookService(encrypted); var eb=ea.State.Notebooks.First().Id;
                ea.Execute(new PutNote(null,eb,"Private target","body","")); var en=ea.State.Notes.Last();
                var ew=new MainWindow(ea,encrypted,encrypted.Files.RootDirectory,false); ew.Show(); Ready(ew);
                Call(ew,"BeginInlineEdit",en.Id); Pump(); var es=Field(ew,"inlineSession")!; var ev=es.GetType().GetProperty("Surface")!.GetValue(es)!; var edit=(TextBox)Field(ev,"body")!; edit.Text="private draft";
                Click((DependencyObject)ev,"Insert note link"); Pump(); var retainedChooser=(Window)Field(ew,"wikiChooser")!; var retainedQuery=(TextBox)Field(ew,"wikiChooserQuery")!; retainedQuery.Text="Private"; var retainedResults=(ListBox)Field(ew,"wikiChooserResults")!;
                Directory.CreateDirectory(Path.Combine(encrypted.Files.RootDirectory,"recovery-draft.json"));
                Check(!(bool)Call(ew,"TryLockVault")! && retainedChooser.IsVisible && retainedQuery.Text=="Private","failed lock leaves chooser and draft usable"); Directory.Delete(Path.Combine(encrypted.Files.RootDirectory,"recovery-draft.json"));
                Check((bool)Call(ew,"TryLockVault")!,"successful lock flushes draft");
                Check(Field(ew,"wikiChooser") is null && !retainedChooser.IsVisible && retainedQuery.Text=="" && retainedResults.ItemsSource is null && Field(ew,"wikiIndex") is null,"lock closes and clears chooser and index before hide");
                encrypted.Unlock("synthetic-wiki-password".AsSpan()); var sentinel=((TextBlock)Field(ew,"status")!).Text; Click((DependencyObject)ev,"Insert note link"); Pump();
                Check(Field(ew,"wikiChooser") is null && ((TextBlock)Field(ew,"status")!).Text==sentinel,"locked insertion retained callback inert despite unlocked provider");
                Call(ew,"ResumePrivateVault"); Pump(); Ready(ew); Check(Field(ew,"wikiChooser") is null && !retainedChooser.IsVisible,"unlock never reopens chooser");
                Set(ew,"confirmNoteLeaveForTest",new Func<MessageBoxResult>(()=>MessageBoxResult.No)); Call(ew,"TryLeave",Enum.Parse(typeof(MainWindow).Assembly.GetType("Threadbook.Desktop.LeaveReason")!,"Navigation"));
                foreach(var layout in Enum.GetValues<NotebookLayout>()) { Call(ew,"ChangeLayout",layout); ew.Width=1180; ew.Height=760; ew.UpdateLayout(); Call(ew,"Responsive"); ew.UpdateLayout();
                    Check(((TextBlock)Field(ew,"readingStatus")!).IsVisible && ((StackPanel)Field(ew,"wikiBacklinks")!).IsVisible,"minimum window footer and backlinks reachable "+layout);
                }
                var editorType=typeof(MainWindow).Assembly.GetType("Threadbook.Desktop.NoteEditor")!;
                var modal=(Window)Activator.CreateInstance(editorType,ew,ea.State.Notebooks,en,(Guid?)eb,new Func<Command,bool>(_=>false),new RecoveryStore(encrypted),ea.State.Revision,encrypted.Files.RootDirectory,null)!; modal.Show(); Pump();
                var ms=Field(modal,"surface")!; var mb=(TextBox)Field(ms,"body")!; mb.Select(0,2); var mt=mb.Text; var mu=mb.CanUndo;
                Click((DependencyObject)ms,"Insert note link"); Pump(); Check(((Window)Field(ew,"wikiChooser")!).Owner==modal,"modal uses same surface chooser adapter"); Click((Window)Field(ew,"wikiChooser")!,"Cancel");
                Check(mb.Text==mt && mb.SelectionStart==0 && mb.SelectionLength==2 && mb.CanUndo==mu,"modal chooser cancellation retains selection undo");
                Click((DependencyObject)ms,"Insert note link"); Pump(); ((ListBox)Field(ew,"wikiChooserResults")!).SelectedIndex=0; Click((Window)Field(ew,"wikiChooser")!,"Choose");
                Check(mb.Text.StartsWith("[[id:"),"modal stable link insertion"); mb.Undo(); Check(mb.Text==mt,"modal insertion single undo"); modal.Close();
                Check((bool)Call(ew,"TryLockVault")!,"lock before private close"); ew.Close();
            }
            CoverageGates(args[0]); PreviewRefreshGates(args[0]);
            Check(dispatcherError is null,"dispatcher clean"); application.Shutdown(); Console.WriteLine($"PASS {checks} assertions"); return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}







