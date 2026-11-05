using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Threadbook.Core;
namespace Threadbook.Desktop;
internal static class BenchmarkFixture
{
    public static void Seed(NotebookService app)
    {
        var b=app.State.Notebooks.First(); var now=DateTimeOffset.UtcNow;
        string unit="# Heading\n**bold** and *italic* with `code`.\n- [x] item\n> quote\n\n";
        string body=string.Concat(Enumerable.Repeat(unit,100000/unit.Length+1))[..100000];
        var notes=Enumerable.Range(0,1000).Select(i=>new Note(Guid.NewGuid(),b.Id,$"Benchmark {i:D4}",i==0?body:"Needle small body "+i,"Fixture",false,now,now)).ToArray();
        var replies=Enumerable.Range(0,2000).Select(i=>new Reply(Guid.NewGuid(),notes[0].Id,null,"Synthetic reply "+i,now,now)).ToArray();
        var result=app.Execute(new ReplaceAll(new Snapshot(1,0,new[]{b},notes,replies))); if(!result.Success) throw new Exception(result.Error!.Message);
    }
}
public sealed partial class MainWindow
{
    public async Task RunBenchmark(double readyMs)
    {
        var metrics=new Dictionary<string,List<double>>();
        async Task Measure(string name, Action action, bool render, bool record)
        {
            var timer=Stopwatch.StartNew(); action(); if(render) { UpdateLayout(); await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Render); }
            timer.Stop(); if(record) { if(!metrics.ContainsKey(name))metrics[name]=new(); metrics[name].Add(timer.Elapsed.TotalMilliseconds); }
        }
        selectedId=app.State.Notes.First(n=>n.Body.Length==100000).Id; notebookId=null; Refresh();
        for(int i=-1;i<20;i++)
        {
            await Measure("search_to_layout_ms",()=>{search.Text=i%2==0?"Benchmark":"Needle"; RefreshFeed();},true,i>=0);
            var note=app.State.Notes.First(n=>n.Body.Length==100000);
            await Measure("durable_save_ms",()=>{if(!Run(new PutNote(note.Id,note.NotebookId,note.Title,note.Body,note.Flair)))throw new Exception(status.Text);},false,i>=0);
            await Measure("save_refresh_layout_ms",()=>RefreshFeed(),true,i>=0);
            var viewer=MarkdownView.Viewer(""); detail.Children.Clear(); detail.Children.Add(viewer);
            await Measure("preview_to_layout_ms",()=>{viewer.Document=MarkdownView.Document(note.Body);},true,i>=0);
        }
        var resultObject=new { startup_to_content_rendered_ms=readyMs, notes=app.State.Notes.Count,replies=app.State.Replies.Count, body_utf16=100000, feed_page=75,reply_page=100, preview_blocks=400, runtime=Environment.Version.ToString(),os=Environment.OSVersion.ToString(),arch=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),machine=Environment.MachineName, metrics };
        File.WriteAllText(Path.Combine(dataDir,"benchmark-"+Guid.NewGuid().ToString("N")+".json"),JsonSerializer.Serialize(resultObject,new JsonSerializerOptions{WriteIndented=true}));
    }
}
