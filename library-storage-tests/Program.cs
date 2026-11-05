using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Threadbook.Core;
using Threadbook.Storage;
try
{
    if (args.SequenceEqual(new[] { "--verify-failure-handling" })) throw new InvalidOperationException("Synthetic entrypoint failure: library-storage-tests");
var root=Path.Combine(Environment.CurrentDirectory,"library-storage-tests","fixtures-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
int passed=0;
void Check(bool value,string name) { if(!value) throw new Exception(name); passed++; Console.WriteLine("PASS "+name); }
void Reject(Action action,string name) { try { action(); } catch(Exception ex) when(ex is DataException or OperationCanceledException or InvalidDataException) { Check(true,name); return; } throw new Exception("Expected rejection: "+name); }
var now=DateTimeOffset.UtcNow; var book=new Notebook(Guid.NewGuid(),"Test",now); var note=new Note(Guid.NewGuid(),book.Id,"Existing","untouched", "",false,now,now);
var snapshot=new Snapshot(1,0,[book],[note],[]);
var capture=typeof(NoteImports).GetMethod("CaptureEntries",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
void Capture(IEnumerable<string> entries,int remaining,CancellationToken token) {
    try { capture.Invoke(null,[entries,remaining,token]); }
    catch(System.Reflection.TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); }
}
int enumerated=0;
IEnumerable<string> ExcessEntries() { for(int i=0;i<100;i++) { enumerated++; yield return "entry"+i; } }
Reject(()=>Capture(ExcessEntries(),3,default),"import capture enforces budget before sorting");
Check(enumerated==4,"import stops enumeration at first excess entry");
using(var cancel=new CancellationTokenSource()) {
    IEnumerable<string> CancelEntries() { enumerated++; cancel.Cancel(); yield return "cancel"; throw new Exception("enumerated after cancellation"); }
    Reject(()=>Capture(CancelEntries(),100,cancel.Token),"import cancellation during capture");
}
foreach (var defect in new[] { "missing", "hash", "length" }) {
    using var fixture = new JsonStore(Path.Combine(root,"attachment-"+defect));
    Check(fixture.Commit(snapshot,-1).Success,"restore defect canonical fixture");
    var id=Guid.NewGuid(); byte[] blob=[1,2,3];
    var info=new AttachmentInfo(id,note.Id,"fixture.bin","application/octet-stream",defect=="length" ? 4 : 3,defect=="hash" ? new string('0',64) : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(blob)));
    Check(fixture.Files.WriteAtomic("library.json",JsonSerializer.SerializeToUtf8Bytes(LibraryState.Empty with {Attachments=[info]}),LibraryStore.MaxBytes,false).Success,"restore defect metadata fixture");
    if(defect!="missing") Check(fixture.Files.WriteAtomic("attachments/"+id.ToString("N")+".blob",blob,100,false).Success,"restore defect blob fixture");
    using var bundle=new MemoryStream(); PortableLibrary.CreateBackup(fixture,bundle); bundle.Position=0;
    var target=Path.Combine(root,"rejected-"+defect);
    Reject(()=>PortableLibrary.Restore(bundle,target),"restore rejects attachment "+defect);
    Check(!Directory.Exists(target),"attachment failure cleans destination "+defect);
}
using(var owner=new JsonStore(Path.Combine(root,"plain"))) {
    Check(owner.Commit(snapshot,-1).Success,"canonical fixture"); var store=new LibraryStore(owner);
    Check(store.Load(snapshot).State.Revision==0,"missing library revision zero");
    var library=new LibraryService(store,()=>snapshot); Check(library.Execute(new PutLibraryTag(null,"test")).Success,"metadata commit");
    Check(!store.Commit(LibraryState.Empty with {Revision=1},0,snapshot).Success,"revision conflict");
    var attachments=new AttachmentStore(owner,library);
    var stage=attachments.Stage(note.Id,new MemoryStream(Encoding.UTF8.GetBytes("opaque bytes")),"test.bin","application/octet-stream");
    Check(attachments.AddToNote(stage).Success,"attachment metadata after blob");
    using(var opened=attachments.OpenRead(stage.Info.Id)) Check(opened.Length==12,"attachment hash verified");
    Check(owner.Files.WriteAtomic("attachments/"+stage.Info.Id.ToString("N")+".blob",new byte[]{1},100,false).Success,"mutate fixture blob");
    Reject(()=>attachments.OpenRead(stage.Info.Id),"mutated blob rejected");
    Check(library.Execute(new PutLibraryTag(null,"unrelated")).Success,"missing or changed old blob does not block metadata");
    Check(attachments.Remove(stage.Info.Id).Success,"remove metadata then blob");
    var bad=attachments.Stage(Guid.NewGuid(),new MemoryStream([1,2]),"bad.bin","application/octet-stream");
    Check(!attachments.AddToNote(bad).Success && !owner.Files.Exists("attachments/"+bad.Info.Id.ToString("N")+".blob"),"failed metadata cleans stage");
    Reject(()=>attachments.Stage(note.Id,new MemoryStream([1]),"../escape","text/plain"),"attachment path refused");
    Reject(()=>attachments.OpenRead(stage.Info.Id,Guid.NewGuid()),"attachment note binding");
    Reject(()=>attachments.Stage(note.Id,new MemoryStream(new byte[AttachmentStore.MaxFileBytes+1]),"large.bin","application/octet-stream"),"actual attachment size budget");
    Reject(()=>attachments.Stage(note.Id,new MemoryStream([1,2,3]),"fake.png","image/png"),"PNG signature validation");
    var png=new byte[33]; new byte[]{137,80,78,71,13,10,26,10}.CopyTo(png,0); png[11]=13; Encoding.ASCII.GetBytes("IHDR").CopyTo(png,12); System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16,4),5000); System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20,4),5000);
    Reject(()=>attachments.Stage(note.Id,new MemoryStream(png),"huge.png","image/png"),"PNG pixel budget");
    var cancellation=new CancellationToken(true); Reject(()=>attachments.Stage(note.Id,new MemoryStream([1]),"cancel","text/plain",cancellation),"attachment cancellation");
    using var archive=new MemoryStream(); PortableLibrary.CreateBackup(owner,archive); archive.Position=0; var restored=Path.Combine(root,"restore"); PortableLibrary.Restore(archive,restored);
    using(var copy=new JsonStore(restored)) Check(copy.Load().Notes.Single().Body=="untouched","plain portable roundtrip");
    archive.Position=0; Reject(()=>PortableLibrary.Restore(archive,restored),"restore refuses existing destination");
    var corrupt=JsonSerializer.SerializeToUtf8Bytes(LibraryState.Empty with {SchemaVersion=2}); Check(owner.Files.WriteAtomic("library.json",corrupt,1000,false).Success,"future fixture");
    Check(store.Load(snapshot).WritesBlocked,"future metadata blocked");
    Check(!new LibraryStore(owner).Commit(LibraryState.Empty with {Revision=1},0,snapshot).Success && owner.Files.Read("library.json",1000).SequenceEqual(corrupt),"commit without load preserves damaged bytes");
    var futureBytes=owner.Files.Read("library.json",1000); Check(futureBytes.SequenceEqual(corrupt),"future original unchanged");
    Check(store.Reset(snapshot).Success && owner.Files.List("").Any(k=>k.StartsWith("library.preserved-")),"explicit reset preserves original");
    var stale=LibraryState.Empty with {Revision=1,Tasks=new[]{new LibraryTask(Guid.NewGuid(),Guid.NewGuid(),"stale",null,false,null)}};
    var staleBytes=JsonSerializer.SerializeToUtf8Bytes(stale); Check(owner.Files.WriteAtomic("library.json",staleBytes,LibraryStore.MaxBytes,false).Success,"stale fixture");
    var projection=store.Load(snapshot); Check(!projection.WritesBlocked && projection.State.Tasks.Count==0 && projection.Warning is not null && owner.Files.Read("library.json",LibraryStore.MaxBytes).SequenceEqual(staleBytes),"stale projection never rewrites file");
    var absent=new AttachmentInfo(Guid.NewGuid(),note.Id,"missing.bin","application/octet-stream",1,new string('0',64));
    Check(!store.Commit(stale with {Revision=2,Tasks=[],Attachments=new[]{absent}},1,snapshot).Success,"new absent blob cannot gain metadata");
    var unknown=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(LibraryState.Empty)).Replace("\"SchemaVersion\":1","\"SchemaVersion\":1,\"Unknown\":true"));
    Check(owner.Files.WriteAtomic("library.json",unknown,LibraryStore.MaxBytes,false).Success && store.Load(snapshot).WritesBlocked,"unknown JSON field rejected");
    var nullData=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(LibraryState.Empty)).Replace("\"Tags\":[]","\"Tags\":null"));
    Check(owner.Files.WriteAtomic("library.json",nullData,LibraryStore.MaxBytes,false).Success && store.Load(snapshot).WritesBlocked,"null collection rejected");
    var bytes=JsonSerializer.SerializeToUtf8Bytes(LibraryState.Empty); var duplicate=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"SchemaVersion\":1","\"SchemaVersion\":1,\"SchemaVersion\":1"));
    Check(owner.Files.WriteAtomic("library.json",duplicate,LibraryStore.MaxBytes,false).Success && store.Load(snapshot).WritesBlocked,"duplicate JSON rejected");
}
var md=Path.Combine(root,"import.md"); File.WriteAllText(md,"# Imported\nhello",new UTF8Encoding(true));
var imported=NoteImports.PrepareMarkdownImport(snapshot,[md],book.Id); Check(imported.Notes.Count==2 && imported.Notes[0]==note && imported.Revision==1,"markdown append preserves existing");
Reject(()=>NoteImports.PrepareMarkdownImport(snapshot,[md],book.Id,new CancellationToken(true)),"import cancellation");
File.WriteAllBytes(md,[0xff]); Reject(()=>NoteImports.PrepareMarkdownImport(snapshot,[md],book.Id),"invalid UTF8 rejected");
var appended=NoteImports.PrepareJsonAppend(snapshot,snapshot); Check(appended.Notebooks.Count==2 && appended.Notes.Count==2 && appended.Notes.Select(n=>n.Id).Distinct().Count()==2 && appended.Notes[0]==note,"JSON append remaps IDs");
void BadZip(string name,Action<ZipArchive> fill) { using var stream=new MemoryStream(); using(var z=new ZipArchive(stream,ZipArchiveMode.Create,true)) fill(z); stream.Position=0; var target=Path.Combine(root,"bad-"+Guid.NewGuid().ToString("N")); Reject(()=>PortableLibrary.Restore(stream,target),name); Check(!Directory.Exists(target),name+" no activation"); }
void DeclaredZip(string name,string key,bool validHash=true,bool extra=false) {
    BadZip(name,z=> { var payload=Encoding.UTF8.GetBytes("{}"); var entry=Guid.NewGuid().ToString("N")+".bin"; using(var s=z.CreateEntry(entry).Open()) s.Write(payload);
        var manifest=new {Version=1,Encrypted=false,Files=new[]{new {Entry=entry,Key=key,Size=payload.Length,Sha256=validHash ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)) : new string('0',64)}}};
        using(var s=z.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(s,manifest); if(extra)z.CreateEntry("undeclared.bin");
    });
}
DeclaredZip("declared path traversal","../threadbook.json");
DeclaredZip("declared unknown class","malicious.exe");
DeclaredZip("declared hash mismatch","threadbook.json",false);
DeclaredZip("undeclared entry","threadbook.json",true,true);
BadZip("null manifest fields",z=>{using var s=z.CreateEntry("manifest.json").Open(); s.Write(Encoding.UTF8.GetBytes("{\"Version\":1,\"Encrypted\":false,\"Files\":null}"));});
BadZip("traversal",z=>z.CreateEntry("../escape"));
BadZip("case duplicate",z=>{z.CreateEntry("one");z.CreateEntry("ONE");});
BadZip("symlink",z=>{var e=z.CreateEntry("link"); e.ExternalAttributes=unchecked((int)0xA1FF0000);});
BadZip("zip budget",z=>{using var s=z.CreateEntry("large").Open(); var b=new byte[1024*1024]; for(int i=0;i<26;i++)s.Write(b);});
using(var encrypted=JsonStore.CreateEncrypted(Path.Combine(root,"encrypted"),"synthetic password")) {
    Check(encrypted.Commit(snapshot,-1).Success,"encrypted fixture");
    using var stream=new MemoryStream(); PortableLibrary.CreateBackup(encrypted,stream); stream.Position=0;
    using(var zip=new ZipArchive(stream,ZipArchiveMode.Read,true)) { bool exposed=false; foreach(var e in zip.Entries.Where(e=>e.Name!="manifest.json")) { using var reader=new StreamReader(e.Open()); exposed|=reader.ReadToEnd().Contains("untouched",StringComparison.Ordinal); } Check(!exposed,"encrypted archive contains no note plaintext"); } stream.Position=0;
    PortableLibrary.Restore(stream,Path.Combine(root,"encrypted-restored"),"synthetic password");
    using var copy=JsonStore.OpenEncrypted(Path.Combine(root,"encrypted-restored"),"synthetic password"); Check(copy.Load().Notes.Single().Body=="untouched","encrypted ciphertext roundtrip");
    stream.Position=0; Reject(()=>PortableLibrary.Restore(stream,Path.Combine(root,"wrong-password"),"wrong"),"wrong password rejected");
    Check(!Directory.Exists(Path.Combine(root,"wrong-password")),"failed encrypted restore cleanup");
}
Console.WriteLine($"{passed} assertions passed; fixtures: {root}");
}
catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
