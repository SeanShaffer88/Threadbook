using System.Text.Json;
using System.Text.Json.Serialization;
using Threadbook.Core;

namespace Threadbook.Storage;

public sealed record RecoveryDraft(int SchemaVersion, Guid DraftId, Guid NotebookId, Guid? NoteId,
    long BaseRevision, string Title, string Body, string Flair, DateTimeOffset UpdatedUtc);
public sealed record RecoverySession(int SchemaVersion, Guid? NotebookId, Guid? NoteId, int Caret, DateTimeOffset UpdatedUtc);

/// <summary>Sidecars only. Caller must serialize operations with its live JsonStore lifetime lock;
/// never dispose that owner concurrently. No canonical mutation, restoration, cleanup, or timers are implicit.</summary>
public sealed class RecoveryStore
{
    public const int MaxDraftBytes = 1024 * 1024;
    private readonly string directory;
    private readonly JsonStore owner;
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true, MaxDepth = 8
    };
    public string DraftPath => Path.Combine(directory, "recovery-draft.json");
    public string SessionPath => Path.Combine(directory, "recovery-session.json");
    public RecoveryStore(JsonStore lockedStore)
    {
        ArgumentNullException.ThrowIfNull(lockedStore); owner = lockedStore;
        directory = Path.GetDirectoryName(owner.PrimaryPath)!;
    }
    public RecoveryDraft? LoadDraft() => Read<RecoveryDraft>(DraftPath, MaxDraftBytes, Validate);
    public RecoverySession? LoadSession() => Read<RecoverySession>(SessionPath, 8192, Validate);
    public Outcome SaveDraft(RecoveryDraft draft) => Save(DraftPath, draft, MaxDraftBytes, Validate);
    public Outcome SaveSession(RecoverySession session) => Save(SessionPath, session, 8192, Validate);
    /// <summary>Explicit user discard or successful-commit cleanup only. May remove corrupt bytes.</summary>
    public Outcome DiscardDraft() => Delete(DraftPath);
    public Outcome DiscardSession() => Delete(SessionPath);
    private void RequireLock()
    {
        try { owner.AssertLiveOwner(); }
        catch (DataException) when (!owner.Files.IsEncrypted)
        { throw new DataException(ErrorCode.Conflict, "Recovery requires the application's live exclusive data lock."); }
    }
    private static void Validate(RecoveryDraft d)
    {
        Version(d.SchemaVersion);
        if (d.DraftId == Guid.Empty || d.NotebookId == Guid.Empty || d.NoteId == Guid.Empty || d.BaseRevision < 0 || d.BaseRevision == long.MaxValue ||
            d.Title is not { Length: <= 200 } || d.Body is not { Length: <= 100000 } || d.Flair is not { Length: <= 40 } || !Time(d.UpdatedUtc))
            throw new DataException(ErrorCode.Validation, "Invalid draft identity, revision, fields, or UTC timestamp.");
    }
    private static void Validate(RecoverySession s)
    {
        Version(s.SchemaVersion);
        if (s.NotebookId == Guid.Empty || s.NoteId == Guid.Empty || (s.NoteId is not null && s.NotebookId is null) || s.Caret < 0 || s.Caret > 100000 || !Time(s.UpdatedUtc))
            throw new DataException(ErrorCode.Validation, "Invalid session identity, caret, or UTC timestamp.");
    }
    private static bool Time(DateTimeOffset time) => time != default && time.Offset == TimeSpan.Zero;
    private static void Version(int version) { if (version != 1) throw new DataException(ErrorCode.UnsupportedVersion, "Unsupported recovery schema; original bytes preserved."); }
    private T? Read<T>(string path, int limit, Action<T> validate) where T : class
    {
        RequireLock();
        try
        {
            if (!owner.Files.Exists(Path.GetFileName(path))) return null;
            byte[] bytes = owner.Files.Read(Path.GetFileName(path), limit);
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Expected object.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in doc.RootElement.EnumerateObject()) if (!names.Add(property.Name)) throw new JsonException("Duplicate property.");
            var data = JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new JsonException("Missing sidecar.");
            validate(data); return data;
        }
        catch (DataException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { throw new DataException(ErrorCode.CorruptData, "Cannot read recovery sidecar; original bytes preserved (" + ex.GetType().Name + ")."); }
    }
    private Outcome Save<T>(string path, T data, int limit, Action<T> validate) where T : class
    {
        try
        {
            RequireLock();
            if (data is null) return Outcome.Fail(ErrorCode.Validation, "Missing recovery data.");
            validate(data);
            // Never automatically destroy unreadable/future sidecars. Explicit discard is required first.
            _ = Read(path, limit, validate);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, Options);
            if (bytes.Length > limit) return Outcome.Fail(ErrorCode.CapacityExceeded, "Recovery sidecar exceeds its byte limit.");
            return owner.Files.WriteAtomic(Path.GetFileName(path), bytes, limit, false);
        }
        catch (DataException ex) { return Outcome.Fail(ex.Code, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return Outcome.Fail(ErrorCode.StorageFailure, "Recovery write failed; existing sidecar was preserved."); }
    }

    private Outcome Delete(string path)
    {
        try { RequireLock(); return owner.Files.Delete(Path.GetFileName(path)); }
        catch (DataException ex) { return Outcome.Fail(ex.Code, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Outcome.Fail(ErrorCode.StorageFailure, "Recovery cleanup failed; retry explicit cleanup later."); }
    }
}
