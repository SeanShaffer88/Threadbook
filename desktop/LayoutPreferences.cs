using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Threadbook.Core;
using Threadbook.Storage;

namespace Threadbook.Desktop;

public enum NotebookLayout { Original, KanbanFlow, MindMapCentral, MagazineMinimal, FocusEditor, WorkspaceTabs }
public enum BoardLane { Inbox, InProgress, Archived }
public sealed record WorkspaceContext(Guid Id, Guid? NotebookId, Guid? NoteId, bool SavedOnly, string Search, NoteSort Sort, Guid? SelectedNoteId);
public sealed record LayoutPreferenceState(int SchemaVersion, NotebookLayout Layout, IReadOnlyDictionary<Guid, BoardLane> Lanes,
    IReadOnlyList<WorkspaceContext> Workspaces, Guid? ActiveWorkspaceId)
{
    public static LayoutPreferenceState Default => new(1, NotebookLayout.Original,
        new ReadOnlyDictionary<Guid, BoardLane>(new Dictionary<Guid, BoardLane>()), Array.AsReadOnly(Array.Empty<WorkspaceContext>()), null);
}
public sealed record PreferenceLoadResult(LayoutPreferenceState State, string? Warning = null, bool PreservedInvalid = false);

/// <summary>UI-thread serialized sidecar access. Keep owningStore live and never dispose it concurrently.
/// No canonical mutation or background activity. Reset requires explicit user confirmation in the UI.</summary>
public sealed class LayoutPreferenceStore
{
    public const int MaxBytes = 2 * 1024 * 1024;
    private readonly JsonStore owner;
    private readonly string directory;
    private bool blocked;
    public string PreferencePath => Path.Combine(directory, "ui-preferences.json");
    public Action? BeforeReplace { get; set; }
    private static readonly JsonSerializerOptions Options = new()
    {
        MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true
    };
    public LayoutPreferenceStore(JsonStore owningStore)
    {
        ArgumentNullException.ThrowIfNull(owningStore);
        owner = owningStore; directory = Path.GetDirectoryName(owner.PrimaryPath)!;
    }
    private void RequireLock()
    {
        try { owner.AssertLiveOwner(); }
        catch (DataException) when (!owner.Files.IsEncrypted)
        { throw new DataException(ErrorCode.Conflict, "Preferences require the application's live exclusive data lock."); }
    }
    public PreferenceLoadResult Load(Snapshot canonical)
    {
        try
        {
            RequireLock();
            if (!owner.Files.Exists("ui-preferences.json")) return new(LayoutPreferenceState.Default,
                blocked ? "Preferences remain blocked until explicit reset." : null, blocked);
            var result = Normalize(Read(), canonical);
            if (blocked) return new(LayoutPreferenceState.Default, "Preferences remain blocked until explicit reset.", true);
            return result;
        }
        catch (Exception ex) when (Handled(ex))
        {
            blocked = true;
            return new(LayoutPreferenceState.Default, "Preferences could not be loaded; original bytes preserved. Explicit reset required (" + ex.GetType().Name + ").", true);
        }
    }
    private LayoutPreferenceState Read()
    {
        var bytes = owner.Files.Read("ui-preferences.json", MaxBytes);
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        CheckProperties(doc.RootElement);
        var state = JsonSerializer.Deserialize<LayoutPreferenceState>(bytes, Options) ?? throw new JsonException("Missing preferences.");
        Validate(state); return state;
    }
    private static void CheckProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new JsonException("Duplicate preference property.");
                if (property.Name == "Lanes" && property.Value.ValueKind == JsonValueKind.Object)
                {
                    var ids = new HashSet<Guid>();
                    foreach (var lane in property.Value.EnumerateObject())
                        if (!Guid.TryParse(lane.Name, out var id) || !ids.Add(id)) throw new JsonException("Invalid or duplicate lane ID.");
                }
                CheckProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) CheckProperties(child);
    }
    private static void Validate(LayoutPreferenceState state)
    {
        if (state.SchemaVersion != 1) throw new DataException(ErrorCode.UnsupportedVersion, "Unsupported preference version.");
        void Check(bool valid) { if (!valid) throw new DataException(ErrorCode.Validation, "Invalid preference identity, scope, or value."); }
        Check(Enum.IsDefined(state.Layout) && state.Lanes is not null && state.Workspaces is not null);
        if (state.Lanes!.Count > 20000 || state.Workspaces!.Count > 12)
            throw new DataException(ErrorCode.CapacityExceeded, "Preference collection limit exceeded.");
        foreach (var lane in state.Lanes) Check(lane.Key != Guid.Empty && Enum.IsDefined(lane.Value));
        var ids = new HashSet<Guid>();
        foreach (var tab in state.Workspaces)
        {
            Check(tab is not null);
            Check(tab!.Id != Guid.Empty && ids.Add(tab.Id) && tab.NotebookId != Guid.Empty && tab.NoteId != Guid.Empty && tab.SelectedNoteId != Guid.Empty);
            Check(tab.Search is { Length: <= 500 } && Enum.IsDefined(tab.Sort));
            Check(tab.NoteId is null || tab.NotebookId is not null);
            Check(tab.NoteId is null || tab.SelectedNoteId is null || tab.SelectedNoteId == tab.NoteId);
        }
        Check(state.ActiveWorkspaceId is null || ids.Contains(state.ActiveWorkspaceId.Value));
    }
    public static PreferenceLoadResult Normalize(LayoutPreferenceState state, Snapshot canonical)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(canonical);
        Validate(state);
        var books = canonical.Notebooks.Select(b => b.Id).ToHashSet();
        var notes = canonical.Notes.ToDictionary(n => n.Id);
        bool changed = false;
        var lanes = new Dictionary<Guid, BoardLane>();
        foreach (var pair in state.Lanes)
            if (notes.ContainsKey(pair.Key)) lanes.Add(pair.Key, pair.Value); else changed = true;
        var tabs = new List<WorkspaceContext>();
        foreach (var tab in state.Workspaces)
        {
            if ((tab.NotebookId is Guid book && !books.Contains(book)) || (tab.NoteId is Guid id && !notes.ContainsKey(id)))
            { changed = true; continue; }
            if (tab.NoteId is Guid note && notes[note].NotebookId != tab.NotebookId)
                throw new DataException(ErrorCode.Validation, "Workspace note belongs to another notebook.");
            var next = tab;
            if (tab.SelectedNoteId is Guid selected)
            {
                if (!notes.TryGetValue(selected, out var selectedNote)) { next = tab with { SelectedNoteId = null }; changed = true; }
                else if (tab.NotebookId is Guid scope && selectedNote.NotebookId != scope)
                    throw new DataException(ErrorCode.Validation, "Workspace selection belongs to another notebook.");
            }
            tabs.Add(next);
        }
        Guid? active = state.ActiveWorkspaceId;
        if (active is Guid activeId && !tabs.Any(t => t.Id == activeId)) { active = null; changed = true; }
        return new(state with { Lanes = new ReadOnlyDictionary<Guid, BoardLane>(lanes), Workspaces = Array.AsReadOnly(tabs.ToArray()), ActiveWorkspaceId = active },
            changed ? "Removed preference references to deleted notes or notebooks." : null);
    }
    public Outcome Save(LayoutPreferenceState state, Snapshot canonical)
    {
        try
        {
            RequireLock();
            if (blocked) return Outcome.Fail(ErrorCode.Conflict, "Preferences were preserved; explicitly reset before saving.");
            // Re-read even if Load was never called: invalid existing bytes must not be overwritten.
            if (owner.Files.Exists("ui-preferences.json"))
            {
                try { _ = Normalize(Read(), canonical); }
                catch (Exception ex) when (Handled(ex)) { blocked = true; return Outcome.Fail(ErrorCode.CorruptData, "Existing preferences preserved; explicitly reset before saving."); }
            }
            var normalized = Normalize(state, canonical);
            return Write(normalized.State, normalized.Warning);
        }
        catch (DataException ex) { return Outcome.Fail(ex.Code, ex.Message); }
        catch (Exception ex) when (Handled(ex)) { return Outcome.Fail(ErrorCode.StorageFailure, "Preferences were not saved; current UI remains usable. Retry saving."); }
    }
    public Outcome Reset(Snapshot canonical)
    {
        try
        {
            RequireLock(); ArgumentNullException.ThrowIfNull(canonical);
            // Copy first: a failed replacement leaves both original and recovery copy intact.
            if (owner.Files.Exists("ui-preferences.json"))
            {
                var backup = "ui-preferences.preserved-" + Guid.NewGuid().ToString("N") + ".json";
                var preserved = owner.Files.PreserveStored("ui-preferences.json", backup, JsonStore.MaxBytes + (owner.Files.IsEncrypted ? 29 : 0));
                if (!preserved.Success) return preserved;
            }
            var result = Write(LayoutPreferenceState.Default, null);
            if (result.Success) blocked = false;
            return result;
        }
        catch (DataException ex) { return Outcome.Fail(ex.Code, ex.Message); }
        catch (Exception ex) when (Handled(ex)) { return Outcome.Fail(ErrorCode.StorageFailure, "Reset failed; existing preferences or preserved copy remain available."); }
    }
    private Outcome Write(LayoutPreferenceState state, string? warning)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Options);
            if (bytes.Length > MaxBytes) return Outcome.Fail(ErrorCode.CapacityExceeded, "Preferences exceed 2 MiB.");
            BeforeReplace?.Invoke();
            var result = owner.Files.WriteAtomic("ui-preferences.json", bytes, MaxBytes, false);
            return result.Success ? Outcome.Ok(warning ?? result.Warning) : result;
        }
        catch (Exception ex) when (Handled(ex)) { return Outcome.Fail(ErrorCode.StorageFailure, "Preferences were not saved; prior file preserved. Retry saving."); }
    }

    private static bool Handled(Exception ex) => ex is DataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
}
