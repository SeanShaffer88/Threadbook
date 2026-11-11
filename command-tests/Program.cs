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

sealed class ShortcutKeyboard : KeyboardDevice
{
    internal ShortcutKeyboard() : base(InputManager.Current) { }
    protected override KeyStates GetKeyStatesFromSystem(Key key) => key is Key.LeftCtrl or Key.LeftShift ? KeyStates.Down : KeyStates.None;
}
static class Program
{
    const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    static object? Field(object o, string name) => o.GetType().GetField(name, Flags)!.GetValue(o);
    static void Set(object o, string name, object? value) => o.GetType().GetField(name, Flags)!.SetValue(o,value);
    static object? Call(object o, string name, params object?[] args) => o.GetType().GetMethod(name,Flags)!.Invoke(o,args);
    static int count;
    static void Check(bool ok,string label) { if(!ok) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
    static void Pump() { var f=new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>f.Continue=false)); Dispatcher.PushFrame(f); }
    static void Press(Window w, Key key) => w.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(w),0,key) { RoutedEvent=Keyboard.PreviewKeyDownEvent });
    static IEnumerable<DependencyObject> Tree(DependencyObject root) { yield return root; for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) foreach(var c in Tree(VisualTreeHelper.GetChild(root,i))) yield return c; }
    static void Capture(Window w,string path) { w.UpdateLayout(); var b=new RenderTargetBitmap((int)w.ActualWidth,(int)w.ActualHeight,96,96,PixelFormats.Pbgra32); b.Render(w); var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(b));using var f=File.Create(path);png.Save(f); }
    [STAThread] static int Main(string[] args)
    {
        try {
            if(args.Length!=1 || !Path.IsPathFullyQualified(args[0]) || Directory.Exists(args[0])) throw new ArgumentException("Absolute NEW fixture directory required.");
            Directory.CreateDirectory(args[0]); var application=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
            Exception? dispatchError=null; application.DispatcherUnhandledException+=(_,e)=>{dispatchError=e.Exception;e.Handled=true;};
            using var store=new JsonStore(Path.Combine(args[0],"plain")); var service=new NotebookService(store);
            var first=service.State.Notebooks.First().Id;
            service.Execute(new PutNotebook(null,"Quickbook")); var book=service.State.Notebooks.Last().Id;
            foreach(var title in new[]{"Alpha","Alphabet","The Alpha"}) Check(service.Execute(new PutNote(null,book,title,"Synthetic fixture","")).Success,"seed " + title);
            var target=service.State.Notes.First().Id;
            var main=new MainWindow(service,store,store.Files.RootDirectory,false);main.Show();Pump();
            var reply=(TextBox)Field(main,"replyBox")!; var search=(TextBox)Field(main,"search")!;
            var commands=(Dictionary<string,Action>)Field(main,"uiCommands")!;
            Check(commands.ContainsKey("Catalog"),"catalog command registered with a stable ID");
            service.Execute(new ToggleSaved(target));
            var library=(LibraryService)Field(main,"library")!;
            library.Execute(new PutLibraryTag(null,"selection"));var tag=library.State.Tags.Last().Id;
            library.Execute(new AssignNoteTags(target,new[]{tag}));
            library.Execute(new PutLibraryTask(null,target,"Overdue",DateTimeOffset.UtcNow.AddDays(-1)));
            Call(main,"ReloadLibrary");
            var filters=new SearchRequest(TagIds:new[]{tag});
            foreach(Guid? scope in new Guid?[]{book,null}) {
                Set(main,"notebookId",scope);Set(main,"savedOnly",true);Set(main,"libraryFilters",filters);Set(main,"overdueOnly",true);
                search.Text="Alpha";Call(main,"Refresh");Set(main,"feedLimit",150);Set(main,"replyLimit",200);
                var card=((Panel)Field(main,"feed")!).Children.OfType<Button>().Single(b=>b.Content is StackPanel);
                Set(main,"selectedId",null);var previous=Field(main,"selectedId");reply.Text="selection draft";int decisions=0;
                Set(main,"confirmLeaveForTest",new Func<bool>(()=>{decisions++;return false;}));
                card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Equals(previous,Field(main,"selectedId")) && Equals(scope,Field(main,"notebookId")) && reply.Text=="selection draft" && search.Text=="Alpha" && Equals(filters,Field(main,"libraryFilters")) && (bool)Field(main,"savedOnly")! && (bool)Field(main,"overdueOnly")! && (int)Field(main,"feedLimit")! ==150 && (int)Field(main,"replyLimit")! ==200,"cancelled card preserves view and pagination");
                Set(main,"confirmLeaveForTest",new Func<bool>(()=>{decisions++;return true;}));card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check((Guid?)Field(main,"selectedId")==target && Equals(scope,Field(main,"notebookId")) && search.Text=="Alpha" && Equals(filters,Field(main,"libraryFilters")) && (bool)Field(main,"savedOnly")! && (bool)Field(main,"overdueOnly")! && (int)Field(main,"feedLimit")! ==150,"card preserves notebook or All notes, Saved, search, tag, overdue and feed page");
                reply.Text="same card draft";Set(main,"replyLimit",200);Call(main,"SelectNote",target);
                Check(reply.Text=="same card draft" && (int)Field(main,"replyLimit")! ==200 && decisions==2,"same card keeps draft and reply page without another leave decision");
                reply.Clear();Set(main,"selectedId",null);
            }
            Set(main,"confirmLeaveForTest",null);
            Set(main,"notebookId",first);Call(main,"Refresh");
            var before=Directory.GetFiles(store.Files.RootDirectory).Where(p=>Path.GetFileName(p)!=".threadbook.lock").ToDictionary(p=>p,p=>File.ReadAllBytes(p));
            var registryType=typeof(MainWindow).Assembly.GetType("Threadbook.Desktop.CommandRegistry")!;
            var entryType=typeof(MainWindow).Assembly.GetType("Threadbook.Desktop.PaletteCommand")!;
            var many=Array.CreateInstance(entryType,10000);
            for(int i=0;i<10000;i++) many.SetValue(Activator.CreateInstance(entryType, new object[]{"id:"+i,"Same title","Notes",new Func<string?>(()=>null),new Func<bool>(()=>true)}),i);
            var ranked=(System.Collections.IEnumerable)registryType.GetMethod("Search",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{many,"same"})!;
            var rankedArray=ranked.Cast<object>().ToArray();Check(rankedArray.Length==100 && (string?)Field(rankedArray[0],"<Id>k__BackingField")=="id:0" && (string?)Field(rankedArray[99],"<Id>k__BackingField")=="id:99","10k targets bounded to 100 with stable ties");
            var shortcut=new KeyEventArgs(new ShortcutKeyboard(),PresentationSource.FromVisual(main),0,Key.P){RoutedEvent=Keyboard.PreviewKeyDownEvent};main.RaiseEvent(shortcut);Check(shortcut.Handled && Field(main,"commandPalette") is Window,"routed CtrlShiftP opens palette");Pump(); var palette=(Window)Field(main,"commandPalette")!;
            Check(palette.Owner==main && palette.IsVisible,"owned palette opens");
            var query=(TextBox)Field(palette,"query")!;var results=(ListBox)Field(palette,"results")!;
            query.Text="Alpha";Check(results.Items.Count==3,"canonical notes outside active notebook indexed");
            string Label(int n) => (string)Field(((ListBoxItem)results.Items[n]).Tag,"<Label>k__BackingField")!;
            Check(Label(0)=="Alpha" && Label(1)=="Alphabet" && Label(2)=="The Alpha","exact prefix substring ranking");
            Check(Directory.GetFiles(store.Files.RootDirectory).Where(p=>Path.GetFileName(p)!=".threadbook.lock").Order().SequenceEqual(before.Keys.Order()) && before.All(kv=>File.ReadAllBytes(kv.Key).SequenceEqual(kv.Value)),"opening and searching do not write store");Press(palette,Key.Down);Check(results.SelectedIndex==1,"routed Down selects");Press(palette,Key.Up);Check(results.SelectedIndex==0,"routed Up selects");
            search.Text="hidden";reply.Text="keep draft";Set(main,"confirmLeaveForTest",new Func<bool>(()=>false));
            var selection=Field(main,"selectedId");var oldBook=Field(main,"notebookId");Press(palette,Key.Enter);
            Check(Equals(selection,Field(main,"selectedId")) && Equals(oldBook,Field(main,"notebookId")) && reply.Text=="keep draft" && search.Text=="hidden","cancelled quickjump preserves context and draft");
            Set(main,"confirmLeaveForTest",new Func<bool>(()=>true));Press(palette,Key.Enter);
            Check((Guid?)Field(main,"selectedId")==target && (Guid?)Field(main,"notebookId")==book && search.Text=="" && reply.Text=="" && !(bool)Field(main,"savedOnly")! && !(bool)Field(main,"overdueOnly")! && Equals(Field(main,"libraryFilters"),new SearchRequest()) && (int)Field(main,"feedLimit")! ==75,"approved quickjump reveals target");
            reply.Text="same note draft";Check((bool)Call(main,"TryNavigate",null,target,false,true)! && reply.Text=="same note draft","same-note draft retained");reply.Clear();
            commands["Command palette"]();palette=(Window)Field(main,"commandPalette")!;query=(TextBox)Field(palette,"query")!;results=(ListBox)Field(palette,"results")!;
            query.Text="Quickbook";Check(results.Items.Count==1,"notebook result");Press(palette,Key.Enter);Check((Guid?)Field(main,"notebookId")==book,"notebook jump executes");
            commands["Command palette"]();palette=(Window)Field(main,"commandPalette")!;query=(TextBox)Field(palette,"query")!;results=(ListBox)Field(palette,"results")!;
            query.Text="Alpha";var stale=((ListBoxItem)results.Items[0]).Tag;service.Execute(new DeleteNote(target));
            var registry=Field(main,"commandRegistry")!;
            var exec=registry.GetType().GetMethods(Flags).Single(m=>m.Name=="TryExecute" && m.GetParameters()[0].ParameterType!=typeof(string));
            Check(!(bool)exec.Invoke(registry,new[]{stale})!,"stale ID rejected");
            Press(palette,Key.Escape);Check(!palette.IsVisible && query.Text=="" && results.Items.Count==0 && palette.Title=="Threadbook","Escape clears private palette data");
            Set(main,"selectedId",null);Check((string?)Call(main,"CommandAvailability","Save reply") == "Choose a note first.","missing selection disabled");
            Set(main,"selectedId",service.State.Notes.First().Id);Check((string?)Call(main,"CommandAvailability","Save reply")=="Write a reply first.","empty reply disabled");
            Check((string?)Call(main,"CommandAvailability","Lock vault")=="This library is not encrypted.","plaintext lock disabled honestly");
            File.WriteAllText(Path.Combine(store.Files.RootDirectory,"archive-operation.json"),"{}");
            Check(Call(main,"CommandAvailability","New note") is string && Call(main,"CommandAvailability","History") is null && Call(main,"CommandAvailability","Trash") is null,"pending blocks writes but permits recovery");
            File.Delete(Path.Combine(store.Files.RootDirectory,"archive-operation.json"));
            reply.Text="protected";var status=((TextBlock)Field(main,"status")!).Text;var revision=service.State.Revision;Set(main,"vaultLocked",true);
            commands["Save reply"]();commands["Theme: Dark / Light / System"]();
            Check(store.Files.IsUnlocked && service.State.Revision==revision && reply.Text=="protected" && ((TextBlock)Field(main,"status")!).Text==status,"retained callbacks reject UI lock with provider unlocked without status mutation");
            Set(main,"vaultLocked",false);reply.Clear();
            // Native menu click and compatibility adapter both execute the registry verb.
            var themeMenu=Tree(main).OfType<MenuItem>().FirstOrDefault(m=>(string?)m.Header=="Theme: Dark / Light / System");
            // Menu items are logical children of the toolbar's top-level menu.
            var menu=Tree(main).OfType<Menu>().First();themeMenu=menu.Items.OfType<MenuItem>().SelectMany(m=>m.Items.OfType<MenuItem>()).Single(m=>(string?)m.Header=="Theme: Dark / Light / System");
            var themeType=typeof(MainWindow).Assembly.GetType("Threadbook.Desktop.Theme")!;string Mode()=>(string)themeType.GetProperty("Mode",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.GetValue(null)!;
            var mode=Mode();themeMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Check(Mode()!=mode,"menu invokes shared registry action");commands["Theme: Dark / Light / System"]();commands["Theme: Dark / Light / System"]();
            commands["Command palette"]();palette=(Window)Field(main,"commandPalette")!;query=(TextBox)Field(palette,"query")!;query.Text="Alpha";Pump();Capture(main,Path.Combine(args[0],"main.png"));Capture(palette,Path.Combine(args[0],"palette.png"));palette.Close();main.Close();
            char[] password="synthetic-palette-lock".ToCharArray();using var encrypted=JsonStore.CreateEncrypted(Path.Combine(args[0],"encrypted"),password);var es=new NotebookService(encrypted);var em=new MainWindow(es,encrypted,encrypted.Files.RootDirectory,false);em.Show();Pump();
            ((Dictionary<string,Action>)Field(em,"uiCommands")!)["Command palette"]();var ep=(Window)Field(em,"commandPalette")!;var eq=(TextBox)Field(ep,"query")!;var er=(ListBox)Field(ep,"results")!;eq.Text="secret";
            Check((bool)Call(em,"TryLockVault")!,"owned open palette does not block vault lock");Check(!ep.IsVisible && eq.Text=="" && er.Items.Count==0,"successful lock closes and clears palette");
            encrypted.Unlock(password);Call(em,"ResumePrivateVault");Pump();Check(Field(em,"commandPalette") is null,"unlock never reopens palette");em.Close();Array.Clear(password);
            if(dispatchError is not null) throw dispatchError;
            Console.WriteLine($"PASS {count} command assertions");application.Shutdown();return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex);return 1; }
    }
}






