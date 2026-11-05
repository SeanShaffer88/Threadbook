using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using Threadbook.Core;
using Threadbook.Storage;
using Threadbook.Desktop;

try
{
    if (args.SequenceEqual(new[] { "--verify-failure-handling" })) throw new InvalidOperationException("Synthetic entrypoint failure: layout-tests");
int passed = 0;
void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
var root = Path.Combine(Path.GetTempPath(), "threadbook-layout-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    using var owner = new JsonStore(root);
    var initial = owner.Load();
    var book = initial.Notebooks[0].Id;
    var now = DateTimeOffset.UtcNow;
    var note = new Note(Guid.NewGuid(), book, "Title", "Canonical body", "", false, now, now);
    var canonical = initial with { Notes = new[] { note }, Revision = initial.Revision + 1 };
    Check(owner.Commit(canonical, initial.Revision).Success);
    var hash = SHA256.HashData(File.ReadAllBytes(owner.PrimaryPath));
    var store = new LayoutPreferenceStore(owner);
    Test("missing preferences use Original and empty frozen collections", () => {
        var loaded = store.Load(canonical); Check(loaded.State.Layout == NotebookLayout.Original && !loaded.PreservedInvalid && loaded.Warning is null);
        Check(loaded.State.Lanes.Count == 0 && loaded.State.Workspaces.Count == 0);
    });
    Test("all six layouts persist across store restart without canonical mutation", () => {
        foreach (var layout in Enum.GetValues<NotebookLayout>()) {
            var state = LayoutPreferenceState.Default with { Layout = layout, Lanes = new Dictionary<Guid, BoardLane> { [note.Id] = BoardLane.Archived } };
            Check(store.Save(state, canonical).Success);
            var loaded = new LayoutPreferenceStore(owner).Load(canonical);
            Check(loaded.State.Layout == layout && loaded.State.Lanes[note.Id] == BoardLane.Archived);
            Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(owner.PrimaryPath))));
        }
    });
    var tab = new WorkspaceContext(Guid.NewGuid(), book, note.Id, true, "needle", NoteSort.Title, note.Id);
    var tabs = new List<WorkspaceContext> { tab };
    var lanes = new Dictionary<Guid, BoardLane> { [note.Id] = BoardLane.InProgress };
    var stateWithTab = new LayoutPreferenceState(1, NotebookLayout.WorkspaceTabs, lanes, tabs, tab.Id);
    Test("workspace context roundtrip and defensive collection copies", () => {
        var normalized = LayoutPreferenceStore.Normalize(stateWithTab, canonical).State;
        tabs.Clear(); lanes.Clear(); Check(normalized.Workspaces.Single() == tab && normalized.Lanes[note.Id] == BoardLane.InProgress);
        Check(((IList<WorkspaceContext>)normalized.Workspaces).IsReadOnly);
        Check(((IDictionary<Guid, BoardLane>)normalized.Lanes).IsReadOnly);
        Check(store.Save(normalized, canonical).Success);
        Check(new LayoutPreferenceStore(owner).Load(canonical).State.Workspaces.Single() == tab);
    });
    Test("deleted entities normalize with warning, prune active tab and lanes", () => {
        var previous = store.Load(canonical).State;
        var result = LayoutPreferenceStore.Normalize(previous, canonical with { Notes = Array.Empty<Note>() });
        Check(result.Warning is not null && result.State.Workspaces.Count == 0 && result.State.Lanes.Count == 0 && result.State.ActiveWorkspaceId is null);
        Check(store.Save(previous, canonical with { Notes = Array.Empty<Note>() }).Success);
    });
    Test("stale notebook contexts and selected notes are safely pruned", () => {
        var context = tab with { NoteId = null };
        var previous = LayoutPreferenceState.Default with { Workspaces = new[] { context }, ActiveWorkspaceId = context.Id };
        var noNotes = LayoutPreferenceStore.Normalize(previous, canonical with { Notes = Array.Empty<Note>() });
        Check(noNotes.State.Workspaces.Single().SelectedNoteId is null && noNotes.Warning is not null);
        var noBooks = LayoutPreferenceStore.Normalize(previous, Snapshot.Empty);
        Check(noBooks.State.Workspaces.Count == 0 && noBooks.Warning is not null);
    });
    Test("failed atomic write retains old bytes and cleans owned temp", () => {
        var before = File.ReadAllBytes(store.PreferencePath);
        store.BeforeReplace = () => throw new IOException("injected");
        Check(!store.Save(LayoutPreferenceState.Default with { Layout = NotebookLayout.FocusEditor }, canonical).Success);
        Check(before.SequenceEqual(File.ReadAllBytes(store.PreferencePath)));
        Check(Directory.GetFiles(root, ".ui-preferences-pending-*").Length == 0);
        store.BeforeReplace = null;
    });
    foreach (var bad in new[] { "{", "null", "{\"SchemaVersion\":2,\"Layout\":0,\"Lanes\":{},\"Workspaces\":[],\"ActiveWorkspaceId\":null}",
        "{\"SchemaVersion\":1,\"SchemaVersion\":1}", new string('x', LayoutPreferenceStore.MaxBytes + 1),
        "{\"SchemaVersion\":1,\"Layout\":0,\"Lanes\":{},\"Workspaces\":[{\"Id\":\"bad\"}],\"ActiveWorkspaceId\":null}" })
    {
        Test("invalid/future/oversized preferences preserved and reset backed up: " + bad.Length, () => {
            File.WriteAllText(store.PreferencePath, bad);
            var current = new LayoutPreferenceStore(owner);
            var bytes = File.ReadAllBytes(current.PreferencePath);
            var loaded = current.Load(canonical); Check(loaded.PreservedInvalid && loaded.Warning is not null && loaded.State.Layout == NotebookLayout.Original);
            Check(!current.Save(LayoutPreferenceState.Default, canonical).Success);
            Check(bytes.SequenceEqual(File.ReadAllBytes(current.PreferencePath)));
            current.BeforeReplace = () => throw new IOException("reset failure");
            Check(!current.Reset(canonical).Success && bytes.SequenceEqual(File.ReadAllBytes(current.PreferencePath)));
            Check(!current.Save(LayoutPreferenceState.Default, canonical).Success);
            current.BeforeReplace = null;
            Check(current.Reset(canonical).Success && !current.Load(canonical).PreservedInvalid);
            Check(Directory.GetFiles(root, "ui-preferences.preserved-*.json").Any(p => bytes.SequenceEqual(File.ReadAllBytes(p))));
        });
    }
    Test("Save without Load also preserves malformed file", () => {
        File.WriteAllText(store.PreferencePath, "bad"); var fresh = new LayoutPreferenceStore(owner);
        Check(!fresh.Save(LayoutPreferenceState.Default, canonical).Success && File.ReadAllText(store.PreferencePath) == "bad");
        Check(fresh.Reset(canonical).Success);
    });
    Test("recursive duplicate properties and excessive JSON depth rejected", () => {
        foreach (var raw in new[] {
            "{\"SchemaVersion\":1,\"Layout\":0,\"Lanes\":{\""+note.Id+"\":0,\""+note.Id+"\":1},\"Workspaces\":[],\"ActiveWorkspaceId\":null}",
            new string('[',17)+"0"+new string(']',17) }) {
            File.WriteAllText(store.PreferencePath, raw); var fresh = new LayoutPreferenceStore(owner);
            Check(fresh.Load(canonical).PreservedInvalid); Check(fresh.Reset(canonical).Success);
        }
    });
    Test("invalid enums IDs active tab scope and collection limits", () => {
        var valid = LayoutPreferenceState.Default with { Workspaces = new[] { tab }, ActiveWorkspaceId = tab.Id };
        foreach (var bad in new[] {
            valid with { Layout = (NotebookLayout)999 }, valid with { SchemaVersion = 2 },
            valid with { ActiveWorkspaceId = Guid.NewGuid() }, valid with { Workspaces = new[] { tab, tab } },
            valid with { Workspaces = new[] { tab with { Id = Guid.Empty } }, ActiveWorkspaceId = null },
            valid with { Workspaces = new[] { tab with { Search = new string('s',501) } } },
            valid with { Workspaces = new[] { tab with { Sort = (NoteSort)99 } } },
            valid with { Workspaces = new[] { tab with { NotebookId = null } } },
            valid with { Workspaces = new[] { tab with { NotebookId = initial.Notebooks[1].Id } } },
            valid with { Workspaces = new[] { tab with { SelectedNoteId = Guid.NewGuid() } } },
            valid with { Workspaces = Enumerable.Range(0,13).Select(_ => tab with { Id = Guid.NewGuid() }).ToArray(), ActiveWorkspaceId = null },
            valid with { Lanes = new Dictionary<Guid,BoardLane> { [Guid.Empty] = BoardLane.Inbox } },
            valid with { Lanes = new Dictionary<Guid,BoardLane> { [note.Id] = (BoardLane)99 } },
            valid with { Lanes = Enumerable.Range(0,20001).ToDictionary(_ => Guid.NewGuid(), _ => BoardLane.Inbox) }
        }) Check(!store.Save(bad, canonical).Success);
    });
    Test("maximum accepted tabs search lanes and input bytes", () => {
        var maximum = LayoutPreferenceState.Default with {
            Workspaces = Enumerable.Range(0,12).Select(_ => tab with { Id = Guid.NewGuid(), Search = new string('s',500) }).ToArray(),
            Lanes = Enumerable.Range(0,20000).ToDictionary(_ => Guid.NewGuid(), _ => BoardLane.Inbox)
        };
        var normalized = LayoutPreferenceStore.Normalize(maximum,canonical);
        Check(normalized.State.Workspaces.Count == 12 && normalized.State.Lanes.Count == 0 && normalized.Warning is not null);
        Check(store.Save(maximum,canonical).Success);
        var json = JsonSerializer.Serialize(LayoutPreferenceState.Default);
        File.WriteAllText(store.PreferencePath,json + new string(' ',LayoutPreferenceStore.MaxBytes-json.Length));
        var current = new LayoutPreferenceStore(owner);
        Check(!current.Load(canonical).PreservedInvalid && current.Save(LayoutPreferenceState.Default,canonical).Success);
    });
    Test("equivalent GUID lane keys cannot overwrite silently", () => {
        string raw = "{\"SchemaVersion\":1,\"Layout\":0,\"Lanes\":{\""+note.Id.ToString("D")+"\":0,\""+note.Id.ToString("N")+"\":1},\"Workspaces\":[],\"ActiveWorkspaceId\":null}";
        File.WriteAllText(store.PreferencePath,raw); var current = new LayoutPreferenceStore(owner);
        Check(current.Load(canonical).PreservedInvalid && File.ReadAllText(store.PreferencePath)==raw);
        Check(current.Reset(canonical).Success);
    });
    Test("disposed owner cannot write", () => {
        var otherPath = Path.Combine(root,"disposed"); var other = new JsonStore(otherPath); var s = other.Load();
        var prefs = new LayoutPreferenceStore(other); other.Dispose();
        Check(!prefs.Save(LayoutPreferenceState.Default,s).Success && !File.Exists(prefs.PreferencePath));
    });
    Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(owner.PrimaryPath))));
    Console.WriteLine($"{passed} preference tests passed.");
}
finally { Directory.Delete(root,true); }
}
catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
