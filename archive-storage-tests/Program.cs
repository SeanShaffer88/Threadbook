using Threadbook.Core;
using Threadbook.Storage;
using System.Text.Json;
try {
if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || !args[0].Contains("work", StringComparison.OrdinalIgnoreCase) || Directory.Exists(args[0])) throw new Exception("Explicit NEW work fixture root required.");
Directory.CreateDirectory(args[0]); int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
var now = DateTimeOffset.UtcNow; var book = new Notebook(Guid.NewGuid(), "Fixture", now); var note = new Note(Guid.NewGuid(), book.Id, "Original", "original body", "flair", true, now, now); var reply = new Reply(Guid.NewGuid(), note.Id, null, "parent", now, now); var child = new Reply(Guid.NewGuid(), note.Id, reply.Id, "child", now, now); var seed = new Snapshot(1, 0, [book], [note], [reply, child]);
using (var owner = new JsonStore(Path.Combine(args[0], "lean-history"))) {
    Check(owner.Commit(seed, -1).Success, "lean seed"); var canonical = new NotebookService(owner);
    var ls = new LibraryService(new LibraryStore(owner), () => canonical.State); var attachments = new AttachmentStore(owner, ls);
    Check(ls.Execute(new PutLibraryTag(null, "current tag")).Success, "lean tag");
    Check(ls.Execute(new AssignNoteTags(note.Id, [ls.State.Tags.Single().Id])).Success, "lean assignment");
    Check(ls.Execute(new PutLibraryTask(null, note.Id, "current task", null)).Success, "lean task");
    var bytes = new byte[5 * 1024 * 1024]; new Random(42).NextBytes(bytes);
    var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
    var blob = attachments.Stage(note.Id, new MemoryStream(bytes), "generated.bin", "application/octet-stream"); Check(attachments.AddToNote(blob).Success, "lean blob");
    var png = attachments.Stage(note.Id, new MemoryStream(image), "generated.png", "image/png"); Check(attachments.AddToNote(png).Success, "lean image");
    var gateway = new ArchiveMutationService(owner, canonical);
    long ArchiveBytes() => Directory.GetFiles(Path.Combine(args[0], "lean-history"), "archive-*" ).Sum(p => new FileInfo(p).Length);
    var timings = new List<double>(); var growth = new List<long>();
    for (int version = 0; version < 4; version++) {
        long startBytes = ArchiveBytes(); var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = gateway.Execute(new PutNote(note.Id, book.Id, "version " + version, "body " + version, "new flair"), gateway.State.Revision, gateway.LibraryState.Revision);
        timer.Stop(); Check(result.Success, "durable measured save " + version); timings.Add(timer.Elapsed.TotalMilliseconds); growth.Add(ArchiveBytes() - startBytes);
    }
    File.WriteAllText(Path.Combine(args[0], "save-metrics.json"), JsonSerializer.Serialize(new { attachmentBytes = bytes.Length + image.Length, saveMs = timings, archiveGrowthBytes = growth }));
    var history = gateway.ListHistory(note.Id); var original = history.Single(e => gateway.ReadArchive(e.Id).Data.Notes.Single().Title == note.Title);
    foreach (var entry in history) {
        var bundle = gateway.ReadArchive(entry.Id);
        Check(entry.Files.Count == 2 && !entry.Files.Any(f => f.Key.StartsWith("archive-blob-")), "history contains only content payload references");
        Check(bundle.Data.Replies.Count == 0 && bundle.Data.Notebooks.Count == 1 && bundle.Data.Notes.Count == 1, "history excludes replies");
        Check(JsonSerializer.Serialize(bundle.Library) == JsonSerializer.Serialize(LibraryState.Empty), "history excludes library snapshots");
        foreach (var file in entry.Files) { var actual = File.ReadAllBytes(Path.Combine(args[0], "lean-history", file.Key)); Check(actual.LongLength == file.Length && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(actual)) == file.Sha256, "history actual payload length and hash"); }
    }
    Check(!Directory.GetFiles(Path.Combine(args[0], "lean-history"), "archive-blob-*").Any(), "no history blob copies on disk");
    Check(gateway.Execute(new ToggleSaved(note.Id), gateway.State.Revision, gateway.LibraryState.Revision).Success, "lean current bookmark");
    Check(gateway.Execute(new PutReply(reply.Id, note.Id, null, "current reply"), gateway.State.Revision, gateway.LibraryState.Revision).Success, "lean current reply");
    var currentReplies = gateway.State.Replies.ToArray(); var currentMeta = gateway.LibraryState;
    Check(gateway.Restore(gateway.PrepareRestore(original.Id, null, gateway.State.Revision, currentMeta.Revision)).Success, "lean history restore");
    Check(gateway.State.Notes.Single().Body == note.Body && !gateway.State.Notes.Single().Saved && gateway.State.Replies.SequenceEqual(currentReplies), "history restores content preserves current bookmark replies");
    var restoredMeta = gateway.LibraryState;
    Check(restoredMeta.Tasks.SequenceEqual(currentMeta.Tasks) && restoredMeta.Tags.SequenceEqual(currentMeta.Tags) && restoredMeta.NoteTags[note.Id].SequenceEqual(currentMeta.NoteTags[note.Id]) && restoredMeta.Attachments.SequenceEqual(currentMeta.Attachments), "history preserves current metadata");
    Check(gateway.ListHistory(note.Id).Count == 5 && gateway.ListHistory(note.Id).All(e => gateway.ReadArchive(e.Id).Data.Replies.Count == 0 && e.Files.Count == 2), "restore archives current content lean");
    foreach (var a in currentMeta.Attachments) { var actual = owner.Files.Read("attachments/" + a.Id.ToString("N") + ".blob", AttachmentStore.MaxFileBytes); Check(actual.LongLength == a.Length && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(actual)).Equals(a.Sha256, StringComparison.OrdinalIgnoreCase), "active attachment hash length preserved"); }
    Check(gateway.Execute(new DeleteNote(note.Id), gateway.State.Revision, gateway.LibraryState.Revision).Success, "lean fixture trash");
    var trash = gateway.ReadArchive(gateway.ListTrash().Single().Id);
    Check(trash.Data.Replies.SequenceEqual(currentReplies) && trash.Library.Attachments.SequenceEqual(currentMeta.Attachments) && trash.Library.Tasks.SequenceEqual(currentMeta.Tasks), "trash retains full graph metadata");
    foreach (var a in currentMeta.Attachments) { var copy = trash.Entry.Files.Single(f => f.Key == "archive-blob-" + a.Id.ToString("N") + ".blob"); var actual = File.ReadAllBytes(Path.Combine(args[0], "lean-history", copy.Key)); Check(actual.LongLength == a.Length && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(actual)).Equals(a.Sha256, StringComparison.OrdinalIgnoreCase), "trash actual copy hash length"); Check(owner.Files.Delete("attachments/" + a.Id.ToString("N") + ".blob").Success, "remove active fixture blob"); }
    Check(gateway.Restore(gateway.PrepareRestore(trash.Entry.Id, null, gateway.State.Revision, gateway.LibraryState.Revision)).Success, "trash recovers after active delete");
    foreach (var a in currentMeta.Attachments) { var actual = owner.Files.Read("attachments/" + a.Id.ToString("N") + ".blob", AttachmentStore.MaxFileBytes); Check(actual.LongLength == a.Length && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(actual)).Equals(a.Sha256, StringComparison.OrdinalIgnoreCase), "trash recovered hash length"); }
    // Older schema-1 History could contain the full graph. Keep it readable/restorable.
    var richKey = "archive-entry-" + trash.Entry.Id.ToString("N") + ".json";
    var richBytes = owner.Files.Read(richKey, 2 * 1024 * 1024);
    try {
        Check(owner.Files.WriteAtomic(richKey, JsonSerializer.SerializeToUtf8Bytes(trash.Entry with { Kind = ArchiveKind.History }), 2 * 1024 * 1024, false).Success, "richer schema1 fixture");
        Check(gateway.ReadArchive(trash.Entry.Id).Data.Replies.SequenceEqual(currentReplies), "richer schema1 history readable");
        Check(gateway.Restore(gateway.PrepareRestore(trash.Entry.Id, null, gateway.State.Revision, gateway.LibraryState.Revision)).Success, "richer schema1 history restorable");
    } finally { Check(owner.Files.WriteAtomic(richKey, richBytes, 2 * 1024 * 1024, false).Success, "richer fixture descriptor restored"); }
}

foreach (var phase in new[] { "archive", "stages", "journal", "canonical", "library", "finalize", "none" }) {
    var path = Path.Combine(args[0], phase);
    using (var owner = new JsonStore(path)) {
        Check(owner.Commit(seed, -1).Success, phase + " seed"); var service = new NotebookService(owner); var gateway = new ArchiveMutationService(owner, service);
        var preview = service.Preview(new DeleteNotebook(book.Id), 0); Check(preview.Outcome.Success && service.State.Notes.Count == 1 && owner.Load().Revision == 0, "preview pure");
        gateway.BeforePhase = p => { if (p == phase) throw new IOException("injected " + phase); };
        var result = gateway.Execute(new DeleteNotebook(book.Id), 0, 0); Check(result.Success == (phase == "none"), phase + " outcome");
        if (phase is "archive" or "stages" or "journal" or "canonical") Check(owner.Load().Notes.Count == 1, phase + " source retained");
    }
    using (var owner = new JsonStore(path)) {
        var gateway = new ArchiveMutationService(owner, new NotebookService(owner)); Check(gateway.ReconcilePending().Success, phase + " restart reconciliation"); Check(gateway.ReconcilePending().Success, phase + " idempotent reconciliation");
        if (phase is "canonical" or "library" or "finalize" or "none") {
            Check(gateway.State.Notes.Count == 0, phase + " deletion completed"); var e = gateway.ListTrash().Single(); Check(gateway.ReadArchive(e.Id).Data.Replies.SequenceEqual(seed.Replies), "exact nested replies");
            var plan = gateway.PrepareRestore(e.Id, null, gateway.State.Revision, gateway.LibraryState.Revision); Check(gateway.Restore(plan).Success, "restore cascade"); Check(gateway.State.Notes.Single() == note && gateway.State.Replies.SequenceEqual(seed.Replies), "exact restored records");
            try { gateway.PrepareRestore(e.Id, null, gateway.State.Revision, gateway.LibraryState.Revision); throw new Exception("collision accepted"); } catch (DataException) { checks++; }
        }
    }
}
using (var owner = JsonStore.CreateEncrypted(Path.Combine(args[0], "encrypted"), "fixture-password")) {
    Check(owner.Commit(seed, -1).Success, "encrypted seed"); var gateway = new ArchiveMutationService(owner, new NotebookService(owner));
    Check(gateway.Execute(new PutNote(note.Id, book.Id, "Edited", "new", ""), 0, 0).Success, "content edit"); var history = gateway.ListHistory(note.Id).Single();
    Check(gateway.Execute(new ToggleSaved(note.Id), 1, 1).Success, "bookmark edit"); Check(gateway.ListHistory(note.Id).Count == 1, "bookmark no history");
    Check(gateway.Restore(gateway.PrepareRestore(history.Id, null, 2, 2)).Success, "history restore"); Check(!gateway.State.Notes.Single().Saved && gateway.State.Replies.Count == 2 && gateway.State.Notes.Single().Body == note.Body, "content restore preserves current state");
    using var bundle = new MemoryStream(); PortableLibrary.CreateBackup(owner, bundle); bundle.Position = 0; PortableLibrary.Restore(bundle, Path.Combine(args[0], "portable"), "fixture-password");
    using var restored = JsonStore.OpenEncrypted(Path.Combine(args[0], "portable"), "fixture-password"); Check(new ArchiveStore(restored).List().Count == 2, "encrypted portable history roundtrip");
    owner.Lock(); Check(!gateway.Execute(new DeleteNote(note.Id), 3, 3).Success, "locked mutation refused");
    try { owner.Unlock("wrong"); throw new Exception("wrong password accepted"); } catch (DataException) { checks++; } owner.Unlock("fixture-password");
}
using (var owner = new JsonStore(Path.Combine(args[0], "metadata"))) {
    Check(owner.Commit(seed, -1).Success, "metadata seed"); var canonical = new NotebookService(owner); var ls = new LibraryService(new LibraryStore(owner), () => canonical.State); var attachments = new AttachmentStore(owner, ls);
    Check(ls.Execute(new PutLibraryTag(null, "tag")).Success, "tag created"); var tag = ls.State.Tags.Single().Id; Check(ls.Execute(new AssignNoteTags(note.Id, [tag])).Success, "tag assigned"); Check(ls.Execute(new PutLibraryTask(null, note.Id, "task", null)).Success, "task created");
    var staged = attachments.Stage(note.Id, new MemoryStream([3, 8, 5]), "test.bin", "application/octet-stream"); Check(attachments.AddToNote(staged).Success, "attachment added"); var gateway = new ArchiveMutationService(owner, canonical); var prior = gateway.LibraryState;
    Check(gateway.Execute(new DeleteNotebook(book.Id), 0, prior.Revision).Success, "delete metadata cascade"); var entry = gateway.ListTrash().Single(); Check(owner.Files.Delete("attachments/" + staged.Info.Id.ToString("N") + ".blob").Success, "active blob removed");
    var bundle = gateway.ReadArchive(entry.Id); Check(bundle.Library.Tasks.SequenceEqual(prior.Tasks) && bundle.Library.Attachments.SequenceEqual(prior.Attachments) && bundle.Library.NoteTags[note.Id].SequenceEqual(prior.NoteTags[note.Id]), "exact archived metadata");
    Check(gateway.Restore(gateway.PrepareRestore(entry.Id, null, 1, prior.Revision + 1)).Success, "metadata restore"); Check(owner.Files.Read("attachments/" + staged.Info.Id.ToString("N") + ".blob", 100).SequenceEqual(new byte[] {3,8,5}), "attachment bytes restored from archive");
    using var encrypted = VaultCopies.CreateEncryptedCopy(owner, Path.Combine(args[0], "encrypted-copy"), "copy-password"); Check(new ArchiveStore(encrypted).ReadArchive(entry.Id).Data.Notes.Single() == note, "encrypted copy archive closure");
    var key = "archive-entry-" + entry.Id.ToString("N") + ".json"; var valid = owner.Files.Read(key, 2000000);
    foreach (var invalid in new[] { "null", "{\"SchemaVersion\":2}", System.Text.Encoding.UTF8.GetString(valid).Replace("\"SchemaVersion\":1", "\"SchemaVersion\":1,\"SchemaVersion\":1") }) {
        Check(owner.Files.WriteAtomic(key, System.Text.Encoding.UTF8.GetBytes(invalid), 2000000, false).Success, "malformed fixture"); Check(!gateway.Execute(new ToggleSaved(note.Id), 2, prior.Revision + 2).Success && owner.Load().Revision == 2, "malformed archive blocks unchanged source");
    }
}
using (var owner = new JsonStore(Path.Combine(args[0], "quota"))) {
    Check(owner.Commit(seed, -1).Success, "quota seed"); var gateway = new ArchiveMutationService(owner, new NotebookService(owner));
    for (int i = 0; i < 20; i++) Check(gateway.Execute(new PutNote(note.Id, book.Id, "Version " + i, "body", ""), i, i).Success, "history quota version " + i);
    Check(!gateway.Execute(new PutNote(note.Id, book.Id, "excess", "body", ""), 20, 20).Success && owner.Load().Revision == 20, "20-version quota blocks source unchanged");
}
foreach (var damage in new[] { "descriptor", "payload", "tamper", "payload-tamper" }) {
    using var owner = new JsonStore(Path.Combine(args[0], "required-" + damage));
    Check(owner.Commit(seed, -1).Success, "required seed"); var gateway = new ArchiveMutationService(owner, new NotebookService(owner));
    gateway.BeforePhase = p => { if (p == "canonical") throw new IOException("stop before canonical"); };
    Check(!gateway.Execute(new DeleteNote(note.Id), 0, 0).Success, "prepared interrupted deletion");
    var entry = gateway.ListTrash().Single(); var key = damage.StartsWith("payload") ? entry.Files.First().Key : "archive-entry-" + entry.Id.ToString("N") + ".json";
    var original = owner.Files.Read(key, 2000000);
    Check(!gateway.PermanentlyDeleteArchive(entry.Id).Success, "public pending removal refused");
    if (damage.EndsWith("tamper")) { var text = System.Text.Encoding.UTF8.GetString(original).Replace("Trash", "History"); if (text == System.Text.Encoding.UTF8.GetString(original)) text = System.Text.Encoding.UTF8.GetString(original).Replace("\"Kind\":1", "\"Kind\":0"); Check(owner.Files.WriteAtomic(key, System.Text.Encoding.UTF8.GetBytes(text + " "), 2000000, false).Success, "tampered descriptor"); }
    else Check(owner.Files.Delete(key).Success, "removed closure fixture");
    gateway.BeforePhase = null;
    Check(!gateway.ReconcilePending().Success && owner.Load().Notes.Count == 1, "missing or changed required closure blocks canonical " + damage);
    try { using var backup = new MemoryStream(); PortableLibrary.CreateBackup(owner, backup); throw new Exception("damaged pending copy accepted"); } catch (DataException) { checks++; }
    Check(owner.Files.WriteAtomic(key, original, 2000000, false).Success, "repair fixture");
    Check(gateway.ReconcilePending().Success && gateway.State.Notes.Count == 0, "repair allows retry");
}
using (var owner = new JsonStore(Path.Combine(args[0], "mutable-preview"))) {
    Check(owner.Commit(seed, -1).Success, "preview seed"); var service = new NotebookService(owner); var mutable = seed.Notes.ToList();
    Check(service.CommitPreview(new(Outcome.Ok(), service.State, seed with { Revision = 1, Notes = mutable })).Success, "valid constructed preview");
    mutable.Clear(); Check(service.State.Notes.Count == 1 && owner.Load().Notes.Count == 1, "preview candidate frozen locally");
    Check(!service.CommitPreview(new(Outcome.Ok(), service.State, service.State with { Revision = 7 })).Success && owner.Load().Revision == 1, "forged revision refused");
}

// Faults run after temporary-file flush and before provider replacement; no OS dialogs.
void ProviderHook(string name, Action<string,string>? hook) => ownerType().GetProperty(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.SetValue(null, hook);
Type ownerType() => typeof(JsonStore).Assembly.GetType("Threadbook.Storage.PrivateFiles")!;
void Budget(JsonStore owner, string name, object value) => typeof(JsonStore).GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(owner, value);
int writeCount = 0;
using (var owner = new JsonStore(Path.Combine(args[0], "write-count"))) {
    Check(owner.Commit(seed, -1).Success, "count seed"); ProviderHook("BeforeWriteForTesting", (root,key) => writeCount++);
    try { Check(new ArchiveMutationService(owner, new NotebookService(owner)).Execute(new DeleteNotebook(book.Id),0,0).Success, "count mutation writes"); } finally { ProviderHook("BeforeWriteForTesting", null); }
}
for (int failAt = 1; failAt <= writeCount; failAt++) {
    var path = Path.Combine(args[0], "write-fault-" + failAt); int seen = 0;
    using (var owner = new JsonStore(path)) {
        Check(owner.Commit(seed,-1).Success, "fault seed"); var gateway = new ArchiveMutationService(owner,new NotebookService(owner));
        ProviderHook("BeforeWriteForTesting", (root,key) => { if (++seen == failAt) throw new IOException("individual provider replacement fault"); });
        try { Check(!gateway.Execute(new DeleteNotebook(book.Id),0,0).Success, "write failure returned " + failAt); } finally { ProviderHook("BeforeWriteForTesting",null); }
        Check(owner.Load().Notes.Count == 1 || gateway.ListTrash().Count == 1, "source or preservation survives " + failAt);
    }
    using (var owner = new JsonStore(path)) { var gateway = new ArchiveMutationService(owner,new NotebookService(owner)); Check(gateway.ReconcilePending().Success, "provider restart retry " + failAt); Check(gateway.ReconcilePending().Success,"provider retry idempotent"); }
}
using (var owner = new JsonStore(Path.Combine(args[0], "cancel"))) {
    Check(owner.Commit(seed,-1).Success,"cancel seed"); var gateway = new ArchiveMutationService(owner,new NotebookService(owner)); gateway.BeforePhase=p=> { if(p=="canonical") throw new IOException("pause"); };
    Check(!gateway.Execute(new DeleteNote(note.Id),0,0).Success,"cancel prepared"); Check(gateway.CancelPending().Success && owner.Load().Notes.Single()==note,"cancel preserves source"); Check(gateway.ListTrash().Count==1,"cancel retains preservation");
}
foreach (var explicitDestination in new[] { false, true }) {
    using var owner = new JsonStore(Path.Combine(args[0], "folders-"+explicitDestination)); Check(owner.Commit(seed,-1).Success,"folder seed");
    var service = new NotebookService(owner); var ls = new LibraryService(new LibraryStore(owner),()=>service.State);
    Check(ls.Execute(new PutLibraryFolder(null,null,"ancestor")).Success,"ancestor"); var ancestor=ls.State.Folders.Single().Id;
    Check(ls.Execute(new PutLibraryFolder(null,ancestor,"assigned")).Success,"assigned child"); var assigned=ls.State.Folders.Single(f=>f.ParentId==ancestor).Id;
    Check(ls.Execute(new PutLibraryFolder(null,null,"unrelated")).Success,"unrelated"); var unrelated=ls.State.Folders.Single(f=>f.Name=="unrelated").Id;
    Check(ls.Execute(new AssignNotebookFolder(book.Id,assigned)).Success,"folder assignment"); var gateway=new ArchiveMutationService(owner,service);
    Check(gateway.Execute(new DeleteNote(note.Id),0,ls.State.Revision).Success,"folder delete"); var entry=gateway.ListTrash().Single();
    Check(gateway.ReadArchive(entry.Id).Library.Folders.Count==2,"only assigned ancestor closure captured"); ls = new LibraryService(new LibraryStore(owner),()=>service.State);
    Check(ls.Execute(new PutLibraryFolder(unrelated,null,"renamed")).Success,"rename unrelated"); Check(ls.Execute(new DeleteLibraryFolder(unrelated)).Success,"remove unrelated");
    if(explicitDestination) Check(ls.Execute(new PutLibraryFolder(assigned,ancestor,"changed assigned")).Success,"assigned changed");
    Check(gateway.Restore(gateway.PrepareRestore(entry.Id,explicitDestination?book.Id:null,gateway.State.Revision,ls.State.Revision)).Success,"folder-independent restore " + explicitDestination);
}
foreach (var kind in new[] { "bytes", "entries" }) {
    using var owner = new JsonStore(Path.Combine(args[0], "budget-"+kind)); Check(owner.Commit(seed,-1).Success,"budget seed"); var gateway=new ArchiveMutationService(owner,new NotebookService(owner));
    Check(ArchiveStore.MaxTotalBytes==512L*1024*1024 && ArchiveStore.MaxEntries==5000,"production budget defaults unchanged");
    if(kind=="bytes") { Budget(owner,"ArchiveByteBudget",1024L); Check(owner.Files.WriteAtomic("archive-stage-"+Guid.NewGuid().ToString("N")+".json",new byte[1024],1024,false).Success,"orphan consumes budget"); }
    else { Budget(owner,"ArchiveEntryBudget",1); Check(gateway.Execute(new PutNote(note.Id,book.Id,"changed","body",""),0,0).Success,"entry budget first capture"); }
    var revision=owner.Load().Revision; Check(!gateway.Execute(new DeleteNote(note.Id),revision,gateway.LibraryState.Revision).Success && owner.Load().Notes.Count==1 && owner.Load().Revision==revision,"capacity preflight keeps source " + kind);
}

using (var owner = new JsonStore(Path.Combine(args[0], "cleanup-fault"))) {
    Check(owner.Commit(seed,-1).Success,"cleanup seed"); var gateway = new ArchiveMutationService(owner,new NotebookService(owner));
    bool fired=false; ProviderHook("BeforeDeleteForTesting",(root,key)=> { if(!fired && key.StartsWith("archive-library-")) { fired=true; throw new IOException("cleanup failure"); } });
    try { Check(!gateway.Execute(new DeleteNote(note.Id),0,0).Success,"cleanup failure reported"); } finally { ProviderHook("BeforeDeleteForTesting",null); }
    Check(owner.Files.Exists("archive-operation.json") && owner.Load().Notes.Count==0,"cleanup journal retained after committed deletion");
    Check(!gateway.PermanentlyDeleteArchive(gateway.ListTrash().Single().Id).Success,"cleanup closure deletion blocked");
    Check(gateway.ReconcilePending().Success && !owner.Files.Exists("archive-operation.json"),"cleanup retry completes after partial stage removal");
}
var restorePath=Path.Combine(args[0],"restore-blob-fault"); Guid restoreEntry;
using (var owner=new JsonStore(restorePath)) {
    Check(owner.Commit(seed,-1).Success,"blob seed"); var service=new NotebookService(owner); var ls=new LibraryService(new LibraryStore(owner),()=>service.State); var attachments=new AttachmentStore(owner,ls);
    for(int i=0;i<2;i++) { var staged=attachments.Stage(note.Id,new MemoryStream(new byte[]{(byte)i,8,9}),"test.bin","application/octet-stream"); Check(attachments.AddToNote(staged).Success,"restore blob added"); }
    var gateway=new ArchiveMutationService(owner,service); Check(gateway.Execute(new DeleteNotebook(book.Id),0,ls.State.Revision).Success,"restore blob delete"); var entry=gateway.ListTrash().Single(); restoreEntry=entry.Id;
    foreach(var attachment in gateway.ReadArchive(entry.Id).Library.Attachments) Check(owner.Files.Delete("attachments/"+attachment.Id.ToString("N")+".blob").Success,"remove active blob");
    int blobs=0; ProviderHook("BeforeWriteForTesting",(root,key)=> { if(key.StartsWith("attachments/") && ++blobs==2) throw new IOException("second blob replacement interrupted"); });
    try { Check(!gateway.Restore(gateway.PrepareRestore(entry.Id,null,gateway.State.Revision,gateway.LibraryState.Revision)).Success,"partial restore blob interruption returned"); } finally { ProviderHook("BeforeWriteForTesting",null); }
    Check(owner.Load().Notes.Count==0 && owner.Files.Exists("archive-operation.json"),"blob interruption keeps canonical unchanged and journal");
}
using(var owner=new JsonStore(restorePath)) {
    var gateway=new ArchiveMutationService(owner,new NotebookService(owner)); Check(gateway.ReconcilePending().Success,"partial restore restart retry");
    var attachments=gateway.ReadArchive(restoreEntry).Library.Attachments;
    Check(attachments.All(a=>owner.Files.Read("attachments/"+a.Id.ToString("N")+".blob",100).Length==3) && gateway.State.Notes.Single()==note,"both restored blobs verified");
}
using(var owner=new JsonStore(Path.Combine(args[0],"history-metadata"))) {
    Check(owner.Commit(seed,-1).Success,"history metadata seed"); var service=new NotebookService(owner); var ls=new LibraryService(new LibraryStore(owner),()=>service.State); var attachments=new AttachmentStore(owner,ls); var gateway=new ArchiveMutationService(owner,service);
    Check(gateway.Execute(new PutNote(note.Id,book.Id,"edited","edited",""),0,0).Success,"history initial version"); var history=gateway.ListHistory(note.Id).Single(); ls=new LibraryService(new LibraryStore(owner),()=>service.State);
    Check(ls.Execute(new PutLibraryTag(null,"current tag")).Success,"current tag"); Check(ls.Execute(new AssignNoteTags(note.Id,[ls.State.Tags.Single().Id])).Success,"current tag assigned"); Check(ls.Execute(new PutLibraryTask(null,note.Id,"current task",null)).Success,"current task");
    attachments=new AttachmentStore(owner,ls); Check(attachments.AddToNote(attachments.Stage(note.Id,new MemoryStream(new byte[]{4,5}),"current.bin","application/octet-stream")).Success,"current attachment"); var prior=ls.State;
    Check(gateway.Restore(gateway.PrepareRestore(history.Id,null,service.State.Revision,prior.Revision)).Success,"history metadata restore"); var after=gateway.LibraryState;
    Check(after.Tasks.SequenceEqual(prior.Tasks) && after.Attachments.SequenceEqual(prior.Attachments) && after.Tags.SequenceEqual(prior.Tags) && after.NoteTags[note.Id].SequenceEqual(prior.NoteTags[note.Id]) && gateway.State.Replies.SequenceEqual(seed.Replies) && gateway.State.Notes.Single().Saved,"history preserves current tags tasks attachments saved replies");
}
using(var owner=new JsonStore(Path.Combine(args[0],"copy-provider-fault"))) {
    Check(owner.Commit(seed,-1).Success,"copy fault seed"); var gateway=new ArchiveMutationService(owner,new NotebookService(owner)); Check(gateway.Execute(new DeleteNote(note.Id),0,0).Success,"copy fault archive");
    var target=Path.Combine(args[0],"copy-provider-target"); bool faulted=false;
    ProviderHook("BeforeWriteForTesting",(root,key)=> { if(root==target && key.StartsWith("archive-data-")) { faulted=true; throw new IOException("encrypted copy provider replacement"); } });
    try { try { using var copy=VaultCopies.CreateEncryptedCopy(owner,target,"copy-password"); throw new Exception("copy provider fault ignored"); } catch(DataException) { Check(faulted && !Directory.Exists(target),"copy provider failure cleans owned target"); } } finally { ProviderHook("BeforeWriteForTesting",null); }
    Check(gateway.ReadArchive(gateway.ListTrash().Single().Id).Data.Notes.Single()==note,"copy failure preserves source closure");
}

foreach(var collision in new[]{"reply","task","attachment","tag","folder"}) {
    using var owner=new JsonStore(Path.Combine(args[0],"collision-"+collision)); Check(owner.Commit(seed,-1).Success,"collision seed"); var service=new NotebookService(owner); var store=new LibraryStore(owner); var ls=new LibraryService(store,()=>service.State); var attachments=new AttachmentStore(owner,ls);
    Check(ls.Execute(new PutLibraryTag(null,"captured")).Success,"collision tag"); Check(ls.Execute(new AssignNoteTags(note.Id,[ls.State.Tags.Single().Id])).Success,"collision assign"); Check(ls.Execute(new PutLibraryTask(null,note.Id,"task",null)).Success,"collision task");
    Check(ls.Execute(new PutLibraryFolder(null,null,"folder")).Success,"collision folder"); Check(ls.Execute(new AssignNotebookFolder(book.Id,ls.State.Folders.Single().Id)).Success,"collision folder assign");
    Check(attachments.AddToNote(attachments.Stage(note.Id,new MemoryStream(new byte[]{9}),"test.bin","application/octet-stream")).Success,"collision attachment");
    var gateway=new ArchiveMutationService(owner,service); Check(gateway.Execute(new DeleteNote(note.Id),0,ls.State.Revision).Success,"collision deleted"); var entry=gateway.ListTrash().Single(); var captured=gateway.ReadArchive(entry.Id).Library;
    Check(service.Execute(new PutNote(null,book.Id,"other","","")).Success,"collision other note"); var other=service.State.Notes.Single();
    if(collision=="reply") Check(service.Execute(new ReplaceAll(service.State with { Replies=[reply with {NoteId=other.Id}] })).Success,"reply collision planted");
    else { var meta=store.ReadRaw(); var next=meta with {Revision=meta.Revision+1};
        next=collision switch {
            "task"=>next with {Tasks=[captured.Tasks.Single() with {NoteId=other.Id}]},
            "attachment"=>next with {Attachments=[captured.Attachments.Single() with {NoteId=other.Id}]},
            "tag"=>next with {Tags=[captured.Tags.Single() with {Name="changed"}]},
            _=>next with {Folders=[captured.Folders.Single() with {Name="changed"}]}
        }; Check(store.Commit(next,meta.Revision,service.State).Success,"metadata collision planted");
    }
    var revision=owner.Load().Revision; try { gateway.PrepareRestore(entry.Id,null,revision,store.ReadRaw().Revision); throw new Exception("collision accepted "+collision); } catch(DataException) { Check(owner.Load().Revision==revision,"collision rejects source unchanged "+collision); }
}
using(var owner=new JsonStore(Path.Combine(args[0],"chosen-destination"))) {
    Check(owner.Commit(seed,-1).Success,"destination seed"); var gateway=new ArchiveMutationService(owner,new NotebookService(owner)); Check(gateway.Execute(new DeleteNotebook(book.Id),0,0).Success,"missing original notebook"); var entry=gateway.ListTrash().Single();
    try { gateway.PrepareRestore(entry.Id,Guid.NewGuid(),1,1); throw new Exception("missing destination accepted"); } catch(DataException) { checks++; }
    Check(gateway.Execute(new PutNotebook(null,"new destination"),1,1).Success,"create chosen destination"); var destination=gateway.State.Notebooks.Single().Id;
    Check(gateway.Restore(gateway.PrepareRestore(entry.Id,destination,2,2)).Success && gateway.State.Notes.Single().NotebookId==destination,"restore to newly chosen destination");
}
foreach(var encrypted in new[]{false,true}) {
    var path=Path.Combine(args[0],"portable-required-"+encrypted);
    using var owner=encrypted?JsonStore.CreateEncrypted(path,"password"):new JsonStore(path); Check(owner.Commit(seed,-1).Success,"portable pending seed"); var gateway=new ArchiveMutationService(owner,new NotebookService(owner));
    gateway.BeforePhase=p=> { if(p=="canonical") throw new IOException("pause"); }; Check(!gateway.Execute(new DeleteNote(note.Id),0,0).Success,"portable prepared journal"); var entry=gateway.ListTrash().Single();
    using var valid=new MemoryStream(); PortableLibrary.CreateBackup(owner,valid); valid.Position=0;
    using var changed=new MemoryStream();
    using(var input=new System.IO.Compression.ZipArchive(valid,System.IO.Compression.ZipArchiveMode.Read,true))
    using(var output=new System.IO.Compression.ZipArchive(changed,System.IO.Compression.ZipArchiveMode.Create,true)) {
        using var manifestStream=input.GetEntry("manifest.json")!.Open(); var manifest=System.Text.Json.Nodes.JsonNode.Parse(manifestStream)!; var files=manifest["Files"]!.AsArray(); var removed=files.Single(f=>f!["Key"]!.GetValue<string>()=="archive-entry-"+entry.Id.ToString("N")+".json")!; var omitted=removed["Entry"]!.GetValue<string>(); files.Remove(removed);
        foreach(var item in input.Entries.Where(e=>e.FullName!="manifest.json" && e.FullName!=omitted)) { using var src=item.Open(); using var dst=output.CreateEntry(item.FullName).Open(); src.CopyTo(dst); }
        using var dstManifest=output.CreateEntry("manifest.json").Open(); JsonSerializer.Serialize(dstManifest,manifest);
    }
    changed.Position=0; var destination=Path.Combine(args[0],"portable-required-target-"+encrypted);
    try { PortableLibrary.Restore(changed,destination,encrypted?"password":null); throw new Exception("portable missing required descriptor accepted"); } catch(DataException) { Check(!Directory.Exists(destination),"portable required descriptor closure rejects and cleans "+encrypted); }
    Check(owner.Files.Delete("archive-entry-"+entry.Id.ToString("N")+".json").Success,"copy missing required fixture");
    try { using var copy=VaultCopies.CreateEncryptedCopy(owner,Path.Combine(args[0],"bad-copy-"+encrypted),"password"); throw new Exception("encrypted copy missing required descriptor accepted"); } catch(DataException) { checks++; }
}

using(var owner=new JsonStore(Path.Combine(args[0],"restore-required-journal"))) {
    Check(owner.Commit(seed,-1).Success,"restore required seed"); var gateway=new ArchiveMutationService(owner,new NotebookService(owner)); Check(gateway.Execute(new DeleteNote(note.Id),0,0).Success,"restore required deleted"); var entry=gateway.ListTrash().Single(); var key="archive-entry-"+entry.Id.ToString("N")+".json"; var descriptor=owner.Files.Read(key,2000000);
    gateway.BeforePhase=p=> { if(p=="canonical") throw new IOException("prepared restore"); }; Check(!gateway.Restore(gateway.PrepareRestore(entry.Id,null,1,1)).Success,"restore journal prepared"); Check(owner.Files.Delete(key).Success,"restore required descriptor removed"); gateway.BeforePhase=null;
    Check(!gateway.ReconcilePending().Success && owner.Load().Notes.Count==0,"restore required closure bound before retry"); Check(owner.Files.WriteAtomic(key,descriptor,2000000,false).Success,"restore descriptor repaired"); Check(gateway.ReconcilePending().Success,"restore required repair retry");
}
using(var owner=new JsonStore(Path.Combine(args[0],"exact-byte-boundary"))) {
    Check(owner.Commit(seed,-1).Success,"exact boundary seed"); Budget(owner,"ArchiveByteBudget",1024L); var orphan="archive-stage-"+Guid.NewGuid().ToString("N")+".json";
    Check(owner.Files.WriteAtomic(orphan,new byte[1024],1024,false).Success,"exact byte orphan fixture"); new ArchiveStore(owner).ValidateAll(); Check(true,"exact aggregate budget accepted");
    Check(owner.Files.WriteAtomic("archive-stage-"+Guid.NewGuid().ToString("N")+".json",new byte[1],1,false).Success,"one over aggregate fixture");
    try { new ArchiveStore(owner).ValidateAll(); throw new Exception("over aggregate accepted"); } catch(DataException) { Check(owner.Load().Notes.Single()==note,"over aggregate rejected source unchanged"); }
}
using(var owner=new JsonStore(Path.Combine(args[0],"oversized-preservation"))) {
    var large=seed with { Notes=[note with {Body=new string('x',100000)}] }; Check(owner.Commit(large,-1).Success,"large valid source seed"); Budget(owner,"ArchiveByteBudget",4096L);
    var gateway=new ArchiveMutationService(owner,new NotebookService(owner)); Check(!gateway.Execute(new DeleteNote(note.Id),0,0).Success && owner.Load().Notes.Single().Body.Length==100000 && !owner.Files.Exists("archive-operation.json"),"oversized preservation preflight retains source");
}
foreach (var count in new[] { 0, 2 }) {
    using var owner = new JsonStore(Path.Combine(args[0], "history-shape-" + count));
    Check(owner.Commit(seed, -1).Success, "history shape seed");
    var gateway = new ArchiveMutationService(owner, new NotebookService(owner));
    Check(gateway.Execute(new PutNote(note.Id, book.Id, "edited", "edited", ""), 0, 0).Success, "history shape capture");
    var entry = gateway.ListHistory(note.Id).Single();
    var dataKey = "archive-data-" + entry.Id.ToString("N") + ".json";
    var malformed = seed with { Notes = count == 0 ? [] : [note, note with { Id = Guid.NewGuid() }], Replies = [] };
    var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(malformed);
    var changed = entry with { NoteIds = malformed.Notes.Select(n => n.Id).ToArray(), Files = entry.Files.Select(r => r.Key == dataKey ? r with { Length = bytes.Length, Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) } : r).ToArray() };
    Check(owner.Files.WriteAtomic(dataKey, bytes, JsonStore.MaxBytes, false).Success, "hash consistent history payload");
    Check(owner.Files.WriteAtomic("archive-entry-" + entry.Id.ToString("N") + ".json", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(changed), 2000000, false).Success, "hash consistent history descriptor");
    void Reject(Action action, string label) { try { action(); throw new Exception("accepted " + label); } catch (DataException ex) { Check(ex.Code == ErrorCode.CorruptData, label + " " + count); } }
    Reject(() => gateway.ReadArchive(entry.Id), "history shape read");
    Reject(() => gateway.PrepareRestore(entry.Id, null, 1, 1), "history shape prepare");
    Check(!gateway.Restore(new ArchiveRestorePlan(entry.Id, null, 1, 1)).Success, "history shape controlled restore outcome " + count);
    using var backup = new MemoryStream(); Reject(() => PortableLibrary.CreateBackup(owner, backup), "history shape portable validation");
    Reject(() => { using var copy = VaultCopies.CreateEncryptedCopy(owner, Path.Combine(args[0], "bad-history-copy-" + count), "fixture-password"); }, "history shape encrypted copy");
}
using (var owner = new JsonStore(Path.Combine(args[0], "before-prepare"))) {
    var canonical = new NotebookService(owner); var gateway = new ArchiveMutationService(owner, canonical);
    var bookId = canonical.State.Notebooks.First().Id; var beforeRevision = canonical.State.Revision;
    var command = new PutNote(null, bookId, "New", "body", "");
    var calls = 0; Snapshot? observed = null;
    var failed = gateway.Execute(command, beforeRevision, 0, candidate => { calls++; observed = candidate; return Outcome.Fail(ErrorCode.StorageFailure, "binding failed"); });
    Check(!failed.Success && calls == 1 && canonical.State.Revision == beforeRevision, "failed callback aborts once");
    Check(!Directory.GetFiles(owner.Files.RootDirectory, "archive-*").Any(), "failed binding has no preservation stages or journal");
    calls = 0;
    var result = gateway.Execute(command, beforeRevision, 0, candidate => {
        calls++; observed = candidate;
        Check(!gateway.Execute(command, beforeRevision, 0).Success, "callback Execute reentry rejected");
        Check(!gateway.ReconcilePending().Success, "callback reconcile reentry rejected");
        Check(!gateway.Restore(new ArchiveRestorePlan(Guid.NewGuid(), null, beforeRevision, 0)).Success, "callback restore reentry rejected");
        bool immutable = false;
        try { ((IList<Note>)candidate.Notes).Add(note); } catch (NotSupportedException) { immutable = true; }
        Check(immutable, "callback snapshot immutable");
        return Outcome.Ok();
    });
    Check(result.Success && calls == 1 && canonical.State.Notes.Single().Id == observed!.Notes.Single().Id && canonical.State.Notes.Single() == observed.Notes.Single(), "exact frozen candidate committed once");
}
using (var owner = new JsonStore(Path.Combine(args[0], "callback-direct-mutation"))) {
    var canonical = new NotebookService(owner); var gateway = new ArchiveMutationService(owner, canonical); var b = canonical.State.Notebooks.First().Id;
    var outcome = gateway.Execute(new PutNote(null, b, "candidate", "", ""), canonical.State.Revision, 0, candidate => canonical.Execute(new PutNote(null, b, "external", "", "")));
    Check(!outcome.Success && canonical.State.Notes.Single().Title == "external" && !Directory.GetFiles(owner.Files.RootDirectory, "archive-*").Any(), "direct canonical mutation invalidates candidate before stages");
}
using (var owner = new JsonStore(Path.Combine(args[0], "callback-library-mutation"))) {
    var canonical = new NotebookService(owner); var gateway = new ArchiveMutationService(owner, canonical); var b = canonical.State.Notebooks.First().Id;
    var metadata = new LibraryService(new LibraryStore(owner), () => canonical.State);
    var outcome = gateway.Execute(new PutNote(null, b, "candidate", "", ""), canonical.State.Revision, 0,
        candidate => metadata.Execute(new PutLibraryTag(null, "external")));
    Check(!outcome.Success && canonical.State.Notes.Count == 0 && !Directory.GetFiles(owner.Files.RootDirectory, "archive-*").Any(), "direct library mutation invalidates candidate before stages");
}
using (var owner = new JsonStore(Path.Combine(args[0], "callback-validation"))) {
    var canonical = new NotebookService(owner); var gateway = new ArchiveMutationService(owner, canonical); var b = canonical.State.Notebooks.First().Id; int calls = 0;
    Check(!gateway.Execute(new PutNote(null, b, "", "", ""), canonical.State.Revision, 0, candidate => { calls++; return Outcome.Ok(); }).Success && calls == 0, "invalid preview does not invoke binding");
    Check(!gateway.Execute(new PutNote(null, b, "valid", "", ""), canonical.State.Revision, 0, candidate => throw new IOException("binding exception")).Success, "callback exception aborts controlled");
    Check(gateway.Execute(new PutNote(null, b, "valid", "", ""), canonical.State.Revision, 0).Success, "callback guard resets after exception and original overload works");
}
Console.WriteLine($"PASS {checks} assertions"); return 0;
} catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
