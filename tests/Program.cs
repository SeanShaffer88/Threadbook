using Threadbook.Core;
using Threadbook.Storage;
try
{
var root = Path.Combine(Path.GetTempPath(), "Threadbook-tests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
int passed = 0;
void Assert(bool b, string m) { if (!b) throw new Exception(m); }
void Ok(Outcome o) => Assert(o.Success, o.Error?.Message ?? "failed");
void Test(string name, Action<string> run) { var p=Path.Combine(root,passed.ToString()); Directory.CreateDirectory(p); run(p); Console.WriteLine("PASS "+name); passed++; }
try {
Test("CRUD, Unicode, replies, search, saved and restart", p => {
 Guid id;
 using(var s=new JsonStore(p)) {
 var a=new NotebookService(s); var b=a.State.Notebooks.First();
 Ok(a.Execute(new PutNotebook(null,"日本語"))); var other=a.State.Notebooks.Last(); Ok(a.Execute(new PutNotebook(other.Id,"Renamed")));
 Ok(a.Execute(new PutNote(null,b.Id,"First","Body 🌱","Idea"))); id=a.State.Notes.Single().Id;
 Ok(a.Execute(new PutReply(null,id,null,"searchable branch"))); var r=a.State.Replies.Single(); Ok(a.Execute(new PutReply(null,id,r.Id,"Child")));
 Assert(a.Query(new(Search:" SEARCHABLE ")).Count==1,"Reply search"); Assert(a.Descendants(r.Id).Count==2,"Descendants");
 Ok(a.Execute(new PutReply(r.Id,id,null,"Edited"))); Ok(a.Execute(new PutNote(id,other.Id,"Changed","Body 🌱","Question"))); Ok(a.Execute(new ToggleSaved(id)));
 Assert(a.Query(new(other.Id,true)).Count==1 && a.Query(new(b.Id)).Count==0,"Move and filters");
 }
 using(var s=new JsonStore(p)) { var a=new NotebookService(s); Assert(a.State.Notes.Single().Saved && a.State.Notes.Single().Body=="Body 🌱","Restart");
 Ok(a.Execute(new DeleteReply(a.State.Replies.Single(r=>r.ParentId is null).Id))); Assert(a.State.Replies.Count==0,"Reply cascade");
 Ok(a.Execute(new DeleteNotebook(a.State.Notebooks.Single(b=>b.Name=="Renamed").Id))); Assert(a.State.Notes.Count==0,"Notebook cascade");
 Ok(a.Execute(new PutNote(null,a.State.Notebooks.First().Id,"Delete","",""))); id=a.State.Notes.Single().Id; Ok(a.Execute(new PutReply(null,id,null,"reply"))); Ok(a.Execute(new DeleteNote(id))); Assert(a.State.Replies.Count==0,"Note cascade"); }
});
Test("validation and bounded reply depth",p=>{
 using var s=new JsonStore(p); var a=new NotebookService(s); var b=a.State.Notebooks.First(); var rev=a.State.Revision;
 Assert(!a.Execute(new PutNotebook(null," IDEAS ")).Success,"Duplicate name"); Assert(!a.Execute(new PutNote(null,b.Id," ","","")).Success,"Blank title");
 Assert(!a.Execute(new PutNote(null,b.Id,"x",new string('a',100001),"")).Success,"Body bound"); Assert(!a.Execute(new PutNote(null,Guid.NewGuid(),"x","","")).Success,"Dangling book"); Assert(a.State.Revision==rev,"Revision changed");
 Ok(a.Execute(new PutNote(null,b.Id,"Z","",""))); var n=a.State.Notes.Last(); Guid? parent=null;
 for(int i=0;i<16;i++){ Ok(a.Execute(new PutReply(null,n.Id,parent,i.ToString()))); parent=a.State.Replies.Last().Id; }
 Assert(!a.Execute(new PutReply(null,n.Id,parent,"too deep")).Success,"Depth limit"); Assert(!a.Execute(new PutReply(null,n.Id,null,new string('a',20001))).Success,"Reply bound");
});
Test("failed save, backup rotation, recovery preserves corrupt bytes",p=>{
 using var s=new JsonStore(p); var a=new NotebookService(s); var before=File.ReadAllBytes(s.PrimaryPath); var rev=a.State.Revision;
 s.BeforeReplace=()=>throw new IOException("injected"); Assert(!a.Execute(new PutNotebook(null,"Failure")).Success,"Failure returned"); Assert(a.State.Revision==rev && File.ReadAllBytes(s.PrimaryPath).SequenceEqual(before),"Atomic failure"); s.BeforeReplace=null;
 for(int i=0;i<8;i++) Ok(a.Execute(new PutNotebook(null,"Book"+i))); Assert(s.Backups().Count==5,"Retention"); foreach(var f in s.Backups()) JsonStore.Read(f);
 var backup=s.Backups().First(); var data=JsonStore.Read(backup); File.WriteAllText(s.PrimaryPath,"{damaged"); s.Recover(backup); Assert(s.Load().Revision==data.Revision && Directory.GetFiles(p,"damaged-*.json").Length==1,"Recovery");
});
Test("exclusive lifetime lock",p=>{ using(var s=new JsonStore(p)){try{using var second=new JsonStore(p); throw new Exception("Second admitted");}catch(DataException e){Assert(e.Code==ErrorCode.Conflict,"Lock code");}} using var reopened=new JsonStore(p); reopened.Load(); });
Test("export/import, malformed, duplicate properties, unsupported schema",p=>{
 using var s=new JsonStore(p); var a=new NotebookService(s); var export=Path.Combine(p,"export.json"); JsonStore.Export(a.State,export,false); var preview=JsonStore.Read(export); var rev=a.State.Revision; Ok(a.Execute(new ReplaceAll(preview))); Assert(a.State.Revision==rev+1,"Import revision"); var bytes=File.ReadAllBytes(s.PrimaryPath);
 foreach(var json in new[]{"{","{\"SchemaVersion\":1,\"SchemaVersion\":1}",System.Text.Encoding.UTF8.GetString(bytes).Replace("\"SchemaVersion\": 1","\"SchemaVersion\": 2")}) {File.WriteAllText(export,json); bool rejected=false;try{JsonStore.Read(export);}catch(DataException){rejected=true;} Assert(rejected && File.ReadAllBytes(s.PrimaryPath).SequenceEqual(bytes),"Import preservation");}
 File.WriteAllText(s.PrimaryPath,"{truncated"); try{s.Load();throw new Exception("Malformed accepted");}catch(DataException){} Assert(File.ReadAllText(s.PrimaryPath)=="{truncated","Corruption preservation");
});
Test("duplicates, cycles, cross-note parents and timestamps",p=>{
 using var s=new JsonStore(p);var a=new NotebookService(s);var b=a.State.Notebooks.First();Ok(a.Execute(new PutNote(null,b.Id,"A","","")));Ok(a.Execute(new PutNote(null,b.Id,"B","","")));var n=a.State.Notes[0];var other=a.State.Notes[1];var t=DateTimeOffset.UtcNow;var x=Guid.NewGuid();var y=Guid.NewGuid();
 var bad=new[]{a.State with{Notes=new[]{n,n}},a.State with{Replies=new[]{new Reply(x,n.Id,y,"1",t,t),new Reply(y,n.Id,x,"2",t,t)}},a.State with{Replies=new[]{new Reply(x,n.Id,null,"1",t,t),new Reply(y,other.Id,x,"2",t,t)}},a.State with{Notes=new[]{n with{Created=default}}}};
 foreach(var data in bad) Assert(!a.Execute(new ReplaceAll(data)).Success,"Invalid accepted");
});
Test("deterministic ordering and immutable snapshot",p=>{
 using var s=new JsonStore(p);var t=DateTimeOffset.UtcNow;var a=new NotebookService(s,()=>t);var b=a.State.Notebooks.First();Ok(a.Execute(new PutNote(null,b.Id,"Zebra","","")));t=t.AddMinutes(1);Ok(a.Execute(new PutNote(null,b.Id,"Alpha","","")));Assert(a.Query(new(Sort:NoteSort.Title)).First().Title=="Alpha","Title");Assert(a.Query(new(Sort:NoteSort.Created)).First().Title=="Alpha","Created");
 var n=a.State.Notes.First();Ok(a.Execute(new PutReply(null,n.Id,null,"first")));t=t.AddMinutes(1);Ok(a.Execute(new PutReply(null,n.Id,null,"second")));Assert(a.Children(n.Id).First().Body=="first","Sibling order");Assert(a.State.Notes is not List<Note>,"Mutable snapshot");
});
Test("bounds, failed import, revision conflict, missing primary and orphan temp",p=>{
 using var s=new JsonStore(p);var a=new NotebookService(s);var initial=a.State;var b=initial.Notebooks.First();
 var tooMany=initial with{Notebooks=Enumerable.Range(0,201).Select(i=>new Notebook(Guid.NewGuid(),"b"+i,DateTimeOffset.UtcNow)).ToArray()};Assert(!a.Execute(new ReplaceAll(tooMany)).Success,"Notebook capacity");
 var bytes=File.ReadAllBytes(s.PrimaryPath);s.BeforeReplace=()=>throw new IOException("import failed");Assert(!a.Execute(new ReplaceAll(initial)).Success && a.State.Revision==initial.Revision && File.ReadAllBytes(s.PrimaryPath).SequenceEqual(bytes),"Import rollback");s.BeforeReplace=null;
 File.WriteAllText(Path.Combine(p,".pending-orphan.json"),"{partial");Assert(s.Load().Revision==initial.Revision,"Orphan changed load");
 var stale=initial with{Revision=initial.Revision+3};JsonStore.Export(stale,s.PrimaryPath,true);Assert(a.Execute(new PutNotebook(null,"Conflict")).Error?.Code==ErrorCode.Conflict,"Conflict expected");
 JsonStore.Export(initial,s.PrimaryPath,true);Ok(a.Execute(new PutNotebook(null,"Backup")));File.Delete(s.PrimaryPath);try{s.Load();throw new Exception("Missing primary reset");}catch(DataException){}Assert(!File.Exists(s.PrimaryPath),"Missing primary overwritten");
});
Test("bounded file parser and null collection/type rejection",p=>{
 var file=Path.Combine(p,"input.json");using(var stream=File.Create(file)){stream.SetLength(JsonStore.MaxBytes+1L);}try{JsonStore.Read(file);throw new Exception("Oversize accepted");}catch(DataException e){Assert(e.Code==ErrorCode.CapacityExceeded,"Oversize code");}
 foreach(var json in new[]{"null","{\"SchemaVersion\":1,\"Revision\":0,\"Notebooks\":null,\"Notes\":[],\"Replies\":[]}","{\"SchemaVersion\":\"1\",\"Revision\":0,\"Notebooks\":[],\"Notes\":[],\"Replies\":[]}"}){File.WriteAllText(file,json);try{JsonStore.Read(file);throw new Exception("Invalid type accepted");}catch(DataException){}}
});
Console.WriteLine($"{passed} suites passed.");
} finally {Directory.Delete(root,true);}
}
catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
