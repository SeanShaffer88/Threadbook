using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Threadbook.Core;
using Threadbook.Storage;
using Threadbook.Desktop;
class Program {
 const BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic;
 [STAThread] static int Main(string[] args){try{
 if(args.Length!=1||!Path.IsPathFullyQualified(args[0])||Directory.Exists(args[0]))throw new Exception("New absolute fixture required");Directory.CreateDirectory(args[0]);
 var application=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};Exception? error=null;application.DispatcherUnhandledException+=(_,e)=>{error=e.Exception;e.Handled=true;};
 System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(application.Dispatcher));
 int n=0;void Check(bool b,string s){if(!b)throw new Exception(s);n++;Console.WriteLine("PASS "+s);}
 object? Field(object o,string s)=>o.GetType().GetField(s,F)!.GetValue(o);void Set(object o,string s,object v)=>o.GetType().GetField(s,F)!.SetValue(o,v);object? Call(object o,string s,params object[] a)=>o.GetType().GetMethod(s,F)!.Invoke(o,a);
 using var store=new JsonStore(Path.Combine(args[0],"plain"));var service=new NotebookService(store);var window=new MainWindow(service,store,store.Files.RootDirectory,false);window.Show();window.UpdateLayout();Call(window,"Responsive");window.UpdateLayout();
 Check((bool)Call(window,"Run",new PutNote(null,service.State.Notebooks.First().Id,"Resizable panes · synthetic fixture","Drag the subtle seam or focus it and use Left / Right.\n\nThis local test preserves the reply draft while switching all six layouts.","Idea"))!,"synthetic screenshot note");Call(window,"SelectNote",service.State.Notes.Last().Id);window.UpdateLayout();var grid=(Grid)Field(window,"columns")!;var handles=(List<Thumb>)Field(window,"paneHandles")!;var reply=(TextBox)Field(window,"replyBox")!;reply.Text="unsaved synthetic reply";
 foreach(var layout in Enum.GetValues<NotebookLayout>()){
 reply.Clear(); Call(window,"ChangeLayout",layout); reply.Text="unsaved synthetic reply";window.UpdateLayout();Call(window,"Responsive");window.UpdateLayout();var handle=handles[1];Check(handle.IsVisible,"visible seam "+layout);Check(Math.Abs(handle.TranslatePoint(new Point(6,0),grid).X-grid.ColumnDefinitions[0].ActualWidth-grid.ColumnDefinitions[1].ActualWidth)<1,"actual seam geometry "+layout);
 double width=grid.ColumnDefinitions[1].ActualWidth;handle.RaiseEvent(new DragStartedEventArgs(0,0));handle.RaiseEvent(new DragDeltaEventArgs(-32,0));window.UpdateLayout();handle.RaiseEvent(new DragCompletedEventArgs(-32,0,false));window.UpdateLayout();Check(grid.ColumnDefinitions[1].ActualWidth<width,"drag actual geometry "+layout);
 var saved=store.Files.Read("pane-sizes.json",65536);var device=Keyboard.PrimaryDevice;var source=PresentationSource.FromVisual(window)!;handle.RaiseEvent(new KeyEventArgs(device,source,0,Key.Right){RoutedEvent=Keyboard.PreviewKeyDownEvent});window.UpdateLayout();Check(!saved.SequenceEqual(store.Files.Read("pane-sizes.json",65536)),"keyboard persisted "+layout);
 saved=store.Files.Read("pane-sizes.json",65536);double desired=grid.ColumnDefinitions[1].ActualWidth;handle.RaiseEvent(new DragStartedEventArgs(0,0));handle.RaiseEvent(new DragDeltaEventArgs(-40,0));window.UpdateLayout();handle.RaiseEvent(new DragCompletedEventArgs(-40,0,true));window.UpdateLayout();Check(saved.SequenceEqual(store.Files.Read("pane-sizes.json",65536)) && Math.Abs(grid.ColumnDefinitions[1].ActualWidth-desired)<1,"cancel restores no IO "+layout);
 window.MinWidth=0;window.Width=900;window.UpdateLayout();Call(window,"Responsive");window.UpdateLayout();Check(!handles[1].IsVisible && saved.SequenceEqual(store.Files.Read("pane-sizes.json",65536)),"narrow no preference writes "+layout);window.Width=1480;window.UpdateLayout();Call(window,"Responsive");window.UpdateLayout();Check(Math.Abs(grid.ColumnDefinitions[1].ActualWidth-desired)<1,"grow restores desire "+layout);Check(ReferenceEquals(reply,Field(window,"replyBox"))&&reply.Text=="unsaved synthetic reply","reply identity and draft "+layout);
 }
 reply.Clear(); Call(window,"ChangeLayout",NotebookLayout.Original);window.UpdateLayout();window.Capture(Path.Combine(args[0],"pane-widths-actual.png"));var bytes=store.Files.Read("pane-sizes.json",65536);Check(store.Files.WriteAtomic("archive-operation.json",System.Text.Encoding.UTF8.GetBytes("{}"),1024,false).Success,"pending fixture");Call(window,"ResizePane",1,320d,true);Check(bytes.SequenceEqual(store.Files.Read("pane-sizes.json",65536)),"pending guard no write");store.Files.Delete("archive-operation.json");Set(window,"vaultLocked",true);handles[1].RaiseEvent(new DragStartedEventArgs(0,0));handles[1].RaiseEvent(new DragDeltaEventArgs(-50,0));handles[1].RaiseEvent(new DragCompletedEventArgs(-50,0,false));Check(bytes.SequenceEqual(store.Files.Read("pane-sizes.json",65536)),"retained locked callback no write");window.Close();
 Check(new PaneSizePreferenceStore(store,()=>true).Load().State.Layouts.Count==6,"reload all six layouts");using(var failedStore=new JsonStore(Path.Combine(args[0],"failed-save"))) { var failedService=new NotebookService(failedStore);var failed=new MainWindow(failedService,failedStore,failedStore.Files.RootDirectory,false);failed.Show();failed.UpdateLayout();Call(failed,"Responsive");var invalid=System.Text.Encoding.UTF8.GetBytes("{broken");failedStore.Files.WriteAtomic("pane-sizes.json",invalid,65536,false);Call(failed,"ResizePane",1,330d,true);Check(((PaneSizeState)Field(failed,"paneState")!).Layouts["Original"]["Feed"]==330 && failedStore.Files.Read("pane-sizes.json",65536).SequenceEqual(invalid) && ((TextBlock)Field(failed,"paneWarning")!).Text.Contains("memory"),"failed save retains desire and reports error preserving bytes");Set(failed,"vaultLocked",true);failed.Close();} using(var smokeStore=new JsonStore(Path.Combine(args[0],"layout-smoke"))) { var smokeService=new NotebookService(smokeStore);var smoke=new MainWindow(smokeService,smokeStore,smokeStore.Files.RootDirectory,false);smoke.Show();var task=smoke.RunLayoutSmokeTest();while(!task.IsCompleted){var frame=new System.Windows.Threading.DispatcherFrame();smoke.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(()=>frame.Continue=false));System.Windows.Threading.Dispatcher.PushFrame(frame);}task.GetAwaiter().GetResult();Check(true,"existing six-layout smoke");Set(smoke,"confirmLeaveForTest",new Func<bool>(()=>true));smoke.Close();} if(error!=null)throw error;Console.WriteLine($"PASS {n} pane UI assertions");application.Shutdown();return 0;
 }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}





