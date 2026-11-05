using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Threadbook.Core;
namespace Threadbook.Storage;

public sealed class LibraryStore(JsonStore owner) : ILibraryStore
{
    public const int MaxBytes = 8 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, MaxDepth = 32 };
    internal static LibraryState Parse(byte[] bytes)
    {
        try {
            if (bytes.Length > MaxBytes) throw new DataException(ErrorCode.CapacityExceeded,"Metadata exceeds 8 MiB.");
            using var doc = JsonDocument.Parse(bytes,new JsonDocumentOptions { MaxDepth=32 });
            // Nullable parent/due fields are permitted by their typed contracts.
            CheckDuplicates(doc.RootElement);
            var state=JsonSerializer.Deserialize<LibraryState>(bytes,Options) ?? throw new JsonException();
            LibraryLogic.Validate(state); return state.Freeze();
        } catch (JsonException) { throw new DataException(ErrorCode.CorruptData,"Invalid library metadata; original bytes preserved."); }
    }
    internal static void CheckDuplicates(JsonElement e) {
        if(e.ValueKind==JsonValueKind.Object) { var names=new HashSet<string>(StringComparer.Ordinal); var ids=new HashSet<Guid>(); foreach(var p in e.EnumerateObject()) { if(!names.Add(p.Name) || (Guid.TryParse(p.Name,out var id) && !ids.Add(id))) throw new JsonException("Duplicate property."); CheckDuplicates(p.Value); } }
        else if(e.ValueKind==JsonValueKind.Array) foreach(var c in e.EnumerateArray()) CheckDuplicates(c);
    }
    public LibraryState ReadRaw() { owner.AssertLiveOwner(); return owner.Files.Exists("library.json") ? Parse(owner.Files.Read("library.json",MaxBytes)) : LibraryState.Empty; }
    public LibraryLoadResult Load(Snapshot canonical) {
        owner.AssertLiveOwner();
        try { return LibraryLogic.Normalize(ReadRaw(),canonical); }
        catch(DataException ex) { return new(LibraryState.Empty,ex.Message,true); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { return new(LibraryState.Empty,"Cannot read library metadata; bytes preserved and writes blocked.",true); }
    }
    public Outcome Commit(LibraryState candidate,long expectedRevision,Snapshot canonical) {
        try {
            owner.AssertLiveOwner(); var prior=ReadRaw();
            if(prior.Revision!=expectedRevision || expectedRevision==long.MaxValue || candidate.Revision!=expectedRevision+1) return Outcome.Fail(ErrorCode.Conflict,"Library revision changed.");
            LibraryLogic.Validate(candidate); Validation.Check(canonical);
            var normalized=LibraryLogic.Normalize(candidate,canonical);
            if(normalized.Warning is not null) return Outcome.Fail(ErrorCode.Validation,"Metadata references missing canonical records.");
            foreach(var a in candidate.Attachments) if(!prior.Attachments.Contains(a)) {
                var blob=owner.Files.Read(AttachmentStore.Key(a.Id),AttachmentStore.MaxFileBytes);
                if(blob.LongLength!=a.Length || !Convert.ToHexString(SHA256.HashData(blob)).Equals(a.Sha256,StringComparison.OrdinalIgnoreCase)) return Outcome.Fail(ErrorCode.CorruptData,"New attachment blob is absent or changed.");
            }
            var bytes=ArchiveStore.Encode(candidate,MaxBytes); _=Parse(bytes);
            return owner.Files.WriteAtomic("library.json",bytes,MaxBytes,true);
        } catch(DataException ex) { return Outcome.Fail(ex.Code,ex.Message); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException) { return Outcome.Fail(ErrorCode.StorageFailure,"Library save failed; existing data preserved."); }
    }
    public Outcome Reset(Snapshot canonical) {
        try { owner.AssertLiveOwner(); Validation.Check(canonical);
            if(owner.Files.Exists("library.json")) {
                var preserved=owner.Files.PreserveStored("library.json","library.preserved-"+Guid.NewGuid().ToString("N")+".json",AttachmentStore.MaxFileBytes+(owner.Files.IsEncrypted ? 29 : 0));
                if(!preserved.Success) return preserved;
            }
            return owner.Files.WriteAtomic("library.json",JsonSerializer.SerializeToUtf8Bytes(LibraryState.Empty,Options),MaxBytes,false);
        } catch(DataException ex) { return Outcome.Fail(ex.Code,ex.Message); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { return Outcome.Fail(ErrorCode.StorageFailure,"Reset failed; original metadata has not been replaced."); }
    }
}
