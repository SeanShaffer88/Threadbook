using System.Text.Json;
using System.Text.Json.Serialization;
using Threadbook.Core;
using Threadbook.Storage;

namespace Threadbook.Desktop;

public sealed record PaneSizeState(int SchemaVersion, Dictionary<string, Dictionary<string, double>> Layouts)
{
    public static PaneSizeState Default => new(1, new());
}
public sealed record PaneSizeLoad(PaneSizeState State, string? Warning = null);

/// <summary>Caller serialized private sidecar. The host guard is rechecked on every write.</summary>
public sealed class PaneSizePreferenceStore(JsonStore owner, Func<bool> canWrite)
{
    public const int MaxBytes = 65536;
    private const string Key = "pane-sizes.json";
    private bool blocked;
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, MaxDepth = 5 };
    public static void Validate(PaneSizeState state)
    {
        if (state.SchemaVersion != 1 || state.Layouts is null || state.Layouts.Count > 6) throw new JsonException("Invalid pane schema.");
        foreach (var (layout, roles) in state.Layouts) {
            if (!Enum.TryParse<NotebookLayout>(layout, out var id) || !Enum.IsDefined(id) || id.ToString() != layout || roles is null) throw new JsonException("Unknown layout.");
            foreach (var (role, width) in roles) {
                if (!(id == NotebookLayout.Original ? role is "Sidebar" or "Feed" : role == "Primary") || !double.IsFinite(width) || width < (role == "Sidebar" ? 200 : 260) || width > 4096) throw new JsonException("Invalid pane width or role.");
            }
        }
    }
    private PaneSizeState Read()
    {
        var bytes = owner.Files.Read(Key, MaxBytes);
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 5 });
        void Unique(JsonElement value) {
            if (value.ValueKind != JsonValueKind.Object) return;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject()) { if (!keys.Add(p.Name)) throw new JsonException("Duplicate pane property."); Unique(p.Value); }
        }
        Unique(doc.RootElement);
        var state = JsonSerializer.Deserialize<PaneSizeState>(bytes, Options) ?? throw new JsonException("Null pane preferences.");
        Validate(state); return state;
    }
    public PaneSizeLoad Load()
    {
        try { owner.AssertLiveOwner(); var state = owner.Files.Exists(Key) ? Read() : PaneSizeState.Default; return blocked ? new(PaneSizeState.Default, "Pane preferences preserved; explicit reset required.") : new(state); }
        catch (Exception ex) when (Handled(ex)) { blocked = true; return new(PaneSizeState.Default, "Pane preferences preserved; explicit reset required."); }
    }
    public Outcome Save(PaneSizeState state)
    {
        try {
            RequireWrite(); Validate(state);
            if (blocked) return Outcome.Fail(ErrorCode.Conflict, "Pane preferences preserved; explicit reset required.");
            if (owner.Files.Exists(Key)) { try { _ = Read(); } catch (Exception ex) when (Handled(ex)) { blocked = true; return Outcome.Fail(ErrorCode.CorruptData, "Pane preferences preserved; explicit reset required."); } }
            return Write(state);
        } catch (Exception ex) when (Handled(ex)) { return Outcome.Fail(ErrorCode.StorageFailure, "Pane widths remain in memory; could not save preferences."); }
    }
    public Outcome Reset()
    {
        try {
            RequireWrite();
            if (owner.Files.Exists(Key)) {
                var kept = owner.Files.PreserveStored(Key, "ui-preferences.preserved-pane-" + Guid.NewGuid().ToString("N") + ".json", JsonStore.MaxBytes + (owner.Files.IsEncrypted ? 29 : 0));
                if (!kept.Success) return kept;
            }
            var result = Write(PaneSizeState.Default); if (result.Success) blocked = false; return result;
        } catch (Exception ex) when (Handled(ex)) { return Outcome.Fail(ErrorCode.StorageFailure, "Pane reset failed; original bytes preserved."); }
    }
    private void RequireWrite() { owner.AssertLiveOwner(); if (!canWrite()) throw new DataException(ErrorCode.Conflict, "Pane preferences unavailable while locked or archive pending."); }
    private Outcome Write(PaneSizeState state) {
        RequireWrite(); var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Options);
        if (bytes.Length > MaxBytes) return Outcome.Fail(ErrorCode.CapacityExceeded, "Pane preferences exceed 64 KiB.");
        return owner.Files.WriteAtomic(Key, bytes, MaxBytes, false);
    }
    private static bool Handled(Exception ex) => ex is DataException or System.IO.IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException;
}
