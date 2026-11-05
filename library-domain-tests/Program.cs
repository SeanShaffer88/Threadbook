using Threadbook.Core;
using System.Security.Cryptography;
using System.Text.Json;

try
{
var count = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
void Reject(Action action, ErrorCode code, string name) { try { action(); } catch (DataException e) { Check(e.Code == code, name); return; } throw new Exception("Expected failure: " + name); }
var now = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
var book = Guid.NewGuid(); var note = Guid.NewGuid(); var reply = Guid.NewGuid();
var snapshot = new Snapshot(1, 7, [new(book, "Synthetic", now)], [new(note, book, "Title", new string('z', 3000) + " deepneedle", "", true, now, now)], [new(reply, note, null, "replyneedle", now, now)]).Freeze();
var baseline = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot));
LibraryLogic.Validate(LibraryState.Empty); Check(true, "empty library");
var store = new FakeStore(); var service = new LibraryService(store, () => snapshot);
Check(service.Execute(new PutLibraryFolder(null, null, "Root")).Success, "create folder");
var root = service.State.Folders.Single().Id;
Check(service.Execute(new PutLibraryFolder(null, root, "Child")).Success, "child folder"); var child = service.State.Folders.Last().Id;
Check(service.Execute(new AssignNotebookFolder(book, child)).Success, "assign notebook");
Check(!service.Execute(new PutLibraryFolder(root, child, "Root")).Success, "reject folder cycle");
Check(service.Execute(new DeleteLibraryFolder(child)).Success && service.State.NotebookFolders[book] == root, "delete promotes notebook");
Check(service.Execute(new PutLibraryFolder(null, root, "Same")).Success && service.Execute(new PutLibraryFolder(null, null, "Same")).Success, "same folder name across parents");
Check(!service.Execute(new DeleteLibraryFolder(root)).Success && service.State.Folders.Any(x => x.Id == root), "promotion collision rejected atomically");
var previous = service.State; store.Fail = true;
Check(!service.Execute(new PutLibraryTag(null, "failed")).Success && ReferenceEquals(previous, service.State), "failed commit preserves published state"); store.Fail = false;
Check(service.Execute(new PutLibraryTag(null, "<literal> #tag")).Success, "literal tag preserved"); var tag = service.State.Tags.Single().Id;
var list = new List<Guid> { tag };
Check(service.Execute(new AssignNoteTags(note, list)).Success, "assign tags"); list.Clear(); Check(service.State.NoteTags[note].Count == 1, "freeze nested lists");
Check(service.Execute(new PutNoteTemplate(null, "Custom", "<title>", "<script>literal</script>", "#flair", [tag])).Success, "literal template content");
Check(service.Execute(new DeleteLibraryTag(tag)).Success && service.State.Templates.Single().TagIds.Count == 0 && service.State.NoteTags[note].Count == 0, "delete tag removes defaults and assignments");
Check(LibraryTemplates.BuiltIns.Count == 4 && LibraryTemplates.BuiltIns.Select(x => x.Id).Distinct().Count() == 4, "stable built-in definitions");
LibraryLogic.Validate(LibraryState.Empty with { Templates = LibraryTemplates.BuiltIns });
Check(service.Execute(new PutLibraryTask(null, note, "Task", now)).Success, "create task"); var taskId = service.State.Tasks.Single().Id;
Check(service.Execute(new CompleteLibraryTask(taskId, true)).Success && service.Execute(new SnoozeLibraryTask(taskId, now.AddDays(1))).Success && service.State.Tasks.Single().Id == taskId, "stable task complete and snooze");
Check(!service.Execute(new PutLibraryTask(taskId, note, "Task", now.ToOffset(TimeSpan.FromHours(1)))).Success, "reject non-UTC dates");
var attachment = new AttachmentInfo(Guid.NewGuid(), note, "attachmentneedle.txt", "text/plain", 25L * 1024 * 1024, new string('a', 64));
Check(service.Execute(new AddLibraryAttachment(attachment)).Success, "attachment max individual boundary");
Check(!service.Execute(new AddLibraryAttachment(attachment with { Id = Guid.NewGuid(), Length = attachment.Length + 1 })).Success, "attachment over boundary");
Check(!service.Execute(new AddLibraryAttachment(attachment with { Id = taskId })).Success, "cross-kind duplicate GUID");
Check(service.State.Revision == store.SuccessfulCommits && store.ExpectedValid, "exactly one revision per successful commit");
var stale = service.State with { Tasks = [new(Guid.NewGuid(), Guid.NewGuid(), "Gone", null, false, null)] };
var normalized = LibraryLogic.Normalize(stale, snapshot);
Check(normalized.Warning is not null && normalized.State.Tasks.Count == 0 && normalized.State.Revision == stale.Revision && stale.Tasks.Count == 1, "stale projection preserves original and revision");
var blocked = new LibraryService(new FakeStore { Block = true }, () => snapshot);
Check(!blocked.Execute(new PutLibraryTag(null, "No")).Success && blocked.WritesBlocked, "blocked metadata stays blocked");
var folders = Enumerable.Range(0, 1000).Select(i => new LibraryFolder(Guid.NewGuid(), null, "F" + i)).ToArray();
LibraryLogic.Validate(LibraryState.Empty with { Folders = folders }); Check(true, "1000 folders allowed");
Reject(() => LibraryLogic.Validate(LibraryState.Empty with { Folders = folders.Append(new(Guid.NewGuid(), null, "Extra")).ToArray() }), ErrorCode.CapacityExceeded, "1001 folders rejected");
var chain = new List<LibraryFolder>(); for (var i = 0; i < 8; i++) chain.Add(new(Guid.NewGuid(), i == 0 ? null : chain[^1].Id, "Depth" + i));
LibraryLogic.Validate(LibraryState.Empty with { Folders = chain }); Check(true, "depth eight allowed");
chain.Add(new(Guid.NewGuid(), chain[^1].Id, "Nine")); Reject(() => LibraryLogic.Validate(LibraryState.Empty with { Folders = chain }), ErrorCode.CapacityExceeded, "depth nine rejected");
var tags = Enumerable.Range(0, 2000).Select(i => new LibraryTag(Guid.NewGuid(), "Tag" + i)).ToArray();
LibraryLogic.Validate(LibraryState.Empty with { Tags = tags, NoteTags = new Dictionary<Guid,IReadOnlyList<Guid>> { [note] = tags.Take(32).Select(x => x.Id).ToArray() } }); Check(true, "2000 tags and 32 assignments allowed");
Reject(() => LibraryLogic.Validate(LibraryState.Empty with { Tags = tags, NoteTags = new Dictionary<Guid,IReadOnlyList<Guid>> { [note] = tags.Take(33).Select(x => x.Id).ToArray() } }), ErrorCode.CapacityExceeded, "33 assignments rejected");
Reject(() => LibraryLogic.Validate(LibraryState.Empty with { Tags = tags.Append(new(Guid.NewGuid(), "Overflow")).ToArray() }), ErrorCode.CapacityExceeded, "2001 tags rejected");
var templates = Enumerable.Range(0, 200).Select(i => new NoteTemplate(Guid.NewGuid(), "T" + i, "Title", new string('x',100000), "", [])).ToArray();
LibraryLogic.Validate(LibraryState.Empty with { Templates = templates }); Check(true, "200 templates 100k body allowed");
Reject(() => LibraryLogic.Validate(LibraryState.Empty with { Templates = [templates[0] with { Body = new string('x',100001) }] }), ErrorCode.Validation, "template body overflow");
Reject(() => LibraryLogic.Validate(LibraryState.Empty with { Templates = templates.Append(new(Guid.NewGuid(), "Extra", "Title", "", "", [])).ToArray() }), ErrorCode.CapacityExceeded, "201 templates rejected");
var tasks = Enumerable.Range(0, 20000).Select(i => new LibraryTask(Guid.NewGuid(), note, new string('t',200), null,false,null)).ToArray();
LibraryLogic.Validate(LibraryState.Empty with { Tasks = tasks }); Check(true, "20k tasks 200 title boundary");
Reject(() => LibraryLogic.Validate(LibraryState.Empty with { Tasks = tasks.Append(new(Guid.NewGuid(),note,"Extra",null,false,null)).ToArray() }), ErrorCode.CapacityExceeded, "20001 tasks rejected");
Reject(() => LibraryLogic.Validate(LibraryState.Empty with { Tasks = [tasks[0] with { Title = new string('t',201) }] }), ErrorCode.Validation, "task title overflow");
var manyAttachments = Enumerable.Range(0,10000).Select(i => attachment with { Id = Guid.NewGuid(), Length = 0 }).ToArray();
LibraryLogic.Validate(LibraryState.Empty with { Attachments = manyAttachments }); Check(true,"10k attachments allowed");
Reject(() => LibraryLogic.Validate(LibraryState.Empty with { Attachments = manyAttachments.Append(attachment with { Id = Guid.NewGuid() }).ToArray() }), ErrorCode.CapacityExceeded,"10001 attachments rejected");
var aggregate = Enumerable.Range(0,21).Select(i => attachment with { Id = Guid.NewGuid(), Length = i == 20 ? 12L*1024*1024 : 25L*1024*1024 }).ToArray();
LibraryLogic.Validate(LibraryState.Empty with { Attachments = aggregate }); Check(true,"512MiB aggregate allowed");
aggregate[20] = aggregate[20] with { Length = aggregate[20].Length + 1 }; Reject(() => LibraryLogic.Validate(LibraryState.Empty with { Attachments = aggregate }), ErrorCode.CapacityExceeded,"aggregate overflow rejected");
var search = LibrarySearch.Search(snapshot, service.State, new("deepneedle")); Check(search.TotalCount == 1 && search.Hits[0].Source == SearchSource.Body && search.Hits[0].Snippet.Length <= 200, "search beyond preview");
search = LibrarySearch.Search(snapshot, service.State, new("replyneedle", UpdatedFromUtc: now, UpdatedBeforeUtc: now.AddTicks(1), SavedOnly:true, HasAttachments:true, HasTasks:true)); Check(search.Hits.Single().ReplyId == reply, "reply match with inclusive/exclusive dates and filters");
Check(LibrarySearch.Search(snapshot, service.State, new("replyneedle", UpdatedBeforeUtc:now)).TotalCount == 0, "exclusive before date");
Check(LibrarySearch.Search(snapshot, service.State, new("attachmentneedle")).Hits.Single().Source == SearchSource.Attachment, "attachment name search");
Check(LibrarySearch.Search(snapshot, service.State, new(TagIds:[Guid.NewGuid()])).TotalCount == 0, "unknown tag matches nothing");
Reject(() => LibrarySearch.Search(snapshot, service.State, new(UpdatedFromUtc:now, UpdatedBeforeUtc:now)), ErrorCode.Validation,"invalid date range rejected");
using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); try { LibrarySearch.Search(snapshot,service.State,new(),cancel.Token); throw new Exception("Expected cancellation"); } catch(OperationCanceledException) { Check(true,"search cancellation"); } }
foreach (var ending in new[] { "\r\n", "\n", "\r" })
{
    var body = "heading" + ending + "- [ ] task" + ending + "tail";
    Check(Checklist.Toggle(body,1,"- [ ] task") == body.Replace("[ ]","[x]"), "checklist preserves " + JsonSerializer.Serialize(ending));
}
Reject(() => Checklist.Toggle("- [ ] changed",0,"- [ ] old"),ErrorCode.Conflict,"stale checklist conflict");
Reject(() => Checklist.Toggle("```md\n- [ ] literal\n```",1,"- [ ] literal"),ErrorCode.Validation,"backtick fence rejected");
Reject(() => Checklist.Toggle("~~~\r- [ ] literal\r~~~",1,"- [ ] literal"),ErrorCode.Validation,"tilde fence rejected");
Reject(() => Checklist.Toggle("\\- [ ] literal",0,"\\- [ ] literal"),ErrorCode.Validation,"escaped literal rejected");
Reject(() => Checklist.Toggle("    - [ ] code",0,"    - [ ] code"),ErrorCode.Validation,"indented code rejected");
Check(Checklist.Toggle("```\ncode\n```\n1. [X] done",3,"1. [X] done").EndsWith("1. [ ] done"),"ordered checklist after fence");
Check(baseline.SequenceEqual(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot))),"legacy snapshot unchanged hash");
var tieNote = snapshot.Notes[0] with { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Title = "Tie" };
var tied = snapshot with { Notes = snapshot.Notes.Append(tieNote).Reverse().ToArray() };
Check(LibrarySearch.Search(tied,LibraryState.Empty,new()).Hits[0].NoteId == tieNote.Id,"deterministic GUID tie ordering");
var andTags = new[] { new LibraryTag(Guid.NewGuid(),"A"), new LibraryTag(Guid.NewGuid(),"B") };
var tagged = LibraryState.Empty with { Tags = andTags, NoteTags = new Dictionary<Guid,IReadOnlyList<Guid>> { [note] = andTags.Select(x => x.Id).ToArray(), [tieNote.Id] = [andTags[0].Id] } };
Check(LibrarySearch.Search(tied,tagged,new(TagIds:andTags.Select(x => x.Id).ToArray())).Hits.Single().NoteId == note,"tag AND semantics");
var mutableFolders = new List<LibraryFolder> { new(Guid.NewGuid(),null,"Before") }; var frozen = (LibraryState.Empty with { Folders = mutableFolders }).Freeze(); mutableFolders.Clear(); Check(frozen.Folders.Count == 1,"freeze outer collections");
Check(service.Execute(new DeleteLibraryTask(taskId)).Success && service.Execute(new RemoveLibraryAttachment(attachment.Id)).Success,"task and attachment removal");
Reject(() => Checklist.Toggle("- [ ] task",9,"- [ ] task"),ErrorCode.Conflict,"missing checklist line conflict");
Console.WriteLine($"PASS {count} assertions");
}
catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }

sealed class FakeStore : ILibraryStore
{
    public bool Fail, Block; public int SuccessfulCommits; public bool ExpectedValid = true;
    public LibraryLoadResult Load(Snapshot canonical) => new(LibraryState.Empty,null,Block);
    public Outcome Commit(LibraryState candidate,long expectedRevision,Snapshot canonical)
    {
        ExpectedValid &= expectedRevision == SuccessfulCommits && candidate.Revision == expectedRevision + 1;
        if (Fail) return Outcome.Fail(ErrorCode.StorageFailure,"Injected commit failure");
        SuccessfulCommits++; return Outcome.Ok();
    }
}
