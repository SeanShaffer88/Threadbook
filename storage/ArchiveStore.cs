using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Threadbook.Core;
namespace Threadbook.Storage;

/// <summary>Bounded immutable archive payloads. All private IO is delegated to the live owner.</summary>
public sealed class ArchiveStore(JsonStore owner)
{
    public const long MaxTotalBytes = 512L * 1024 * 1024;
    public const int MaxEntries = 5000;
    internal const int DescriptorLimit = 2 * 1024 * 1024;
    internal static string EntryKey(Guid id) => $"archive-entry-{id:N}.json";
    internal static string DataKey(Guid id) => $"archive-data-{id:N}.json";
    internal static string LibraryKey(Guid id) => $"archive-library-{id:N}.json";
    internal static string BlobKey(Guid id) => $"archive-blob-{id:N}.blob";
    internal static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message) { if (!value) throw new DataException(ErrorCode.CorruptData, message); }
    internal static void Check(Outcome result) { if (!result.Success) throw new DataException(result.Error!.Code, result.Error.Message); }
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static byte[] Encode<T>(T value, int limit, JsonSerializerOptions? options = null)
    {
        using var output = new BoundedStream(limit);
        JsonSerializer.Serialize(output, value, options ?? LibraryStore.Options); return output.ToArray();
    }
    private sealed class BoundedStream(int limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Guard(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Guard(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Guard(1); base.WriteByte(value); }
        private void Guard(int count) { if (Length + count > limit) throw new DataException(ErrorCode.CapacityExceeded, "Archive payload exceeds its byte budget."); }
    }
    internal static T Decode<T>(byte[] bytes)
    {
        try {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            LibraryStore.CheckDuplicates(doc.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, LibraryStore.Options) ?? throw new JsonException();
        } catch (JsonException) { throw new DataException(ErrorCode.CorruptData, "Malformed archive; original bytes preserved."); }
    }
    public IReadOnlyList<ArchiveEntry> List()
    {
        owner.AssertLiveOwner(); var keys = owner.Files.List("archive-entry-");
        if (keys.Count > Math.Min(MaxEntries, owner.ArchiveEntryBudget)) throw new DataException(ErrorCode.CapacityExceeded, "Archive entry capacity exceeded.");
        return Array.AsReadOnly(keys.Select(k => ReadDescriptor(k)).OrderByDescending(e => e.CapturedUtc).ThenBy(e => e.Id).ToArray());
    }
    private ArchiveEntry ReadDescriptor(string key)
    {
        var e = Decode<ArchiveEntry>(owner.Files.Read(key, DescriptorLimit));
        if (e.SchemaVersion != 1) throw new DataException(ErrorCode.UnsupportedVersion, "Unsupported archive schema.");
        Require(e.Id != Guid.Empty && key == EntryKey(e.Id) && Enum.IsDefined(e.Kind) && e.CapturedUtc != default && e.CapturedUtc.Offset == TimeSpan.Zero, "Invalid archive identity.");
        Require(e.NoteIds is { Count: >= 0 and <= 10000 } && e.NoteIds.All(i => i != Guid.Empty) && e.NoteIds.Distinct().Count() == e.NoteIds.Count, "Invalid archived note identities.");
        Require(e.Files is { Count: >= 2 and <= 10002 }, "Missing archive references.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in e.Files) {
            Require(r is not null && r.Key is not null && names.Add(r.Key) && (r.Key == DataKey(e.Id) || r.Key == LibraryKey(e.Id) || Regex.IsMatch(r.Key, @"\Aarchive-blob-[0-9a-f]{32}\.blob\z")) && r.Length >= 0 && r.Length <= JsonStore.MaxBytes && r.Sha256 is not null && Regex.IsMatch(r.Sha256, @"\A[0-9A-F]{64}\z"), "Invalid archive reference.");
        }
        Require(names.Contains(DataKey(e.Id)) && names.Contains(LibraryKey(e.Id)), "Missing archive payload references.");
        return e with { NoteIds = Array.AsReadOnly(e.NoteIds.ToArray()), Files = Array.AsReadOnly(e.Files.ToArray()) };
    }
    internal byte[] Verify(ArchiveReference reference)
    {
        var bytes = owner.Files.Read(reference.Key, reference.Key.StartsWith("archive-library-", StringComparison.Ordinal) ? LibraryStore.MaxBytes : JsonStore.MaxBytes);
        Require(bytes.LongLength == reference.Length && Hash(bytes) == reference.Sha256, "Archive payload hash or length mismatch."); return bytes;
    }
    public ArchiveBundle ReadArchive(Guid id)
    {
        var entry = ReadDescriptor(EntryKey(id));
        foreach (var r in entry.Files) _ = Verify(r);
        var data = JsonStore.Parse(owner.Files.Read(DataKey(id), JsonStore.MaxBytes));
        var library = LibraryStore.Parse(owner.Files.Read(LibraryKey(id), LibraryStore.MaxBytes));
        Require(entry.Kind != ArchiveKind.History || data.Notes.Count == 1, "History must contain exactly one note.");
        Require(data.Notes.Select(n => n.Id).Order().SequenceEqual(entry.NoteIds.Order()), "Archive note references mismatch.");
        Require(LibraryLogic.Normalize(library, data).Warning is null, "Archive metadata references missing records.");
        foreach (var a in library.Attachments) {
            Require(entry.Files.Any(r => r.Key == BlobKey(a.Id) && r.Length == a.Length && r.Sha256.Equals(a.Sha256, StringComparison.OrdinalIgnoreCase)), "Missing archive attachment reference.");
        }
        return new(entry, data, library);
    }
    public void ValidateAll()
    {
        owner.AssertLiveOwner(); long total = 0;
        foreach (var key in owner.Files.List("archive-")) {
            total = checked(total + owner.Files.Read(key, key.StartsWith("archive-entry-", StringComparison.Ordinal) || key == "archive-operation.json" ? DescriptorLimit : key.StartsWith("archive-library-", StringComparison.Ordinal) ? LibraryStore.MaxBytes : JsonStore.MaxBytes).Length);
            if (total > Math.Min(MaxTotalBytes, owner.ArchiveByteBudget)) throw new DataException(ErrorCode.CapacityExceeded, "Archive exceeds 512 MiB.");
        }
        foreach (var e in List()) _ = ReadArchive(e.Id);
        if (owner.Files.Exists("archive-operation.json")) ArchiveMutationService.ValidateJournal(owner);
    }
    internal ArchiveEntry Capture(ArchiveKind kind, Snapshot data, LibraryState library)
    {
        ValidateAll(); var existing = List();
        if (existing.Count >= Math.Min(MaxEntries, owner.ArchiveEntryBudget) || (kind == ArchiveKind.History && data.Notes.Any(n => existing.Count(e => e.Kind == kind && e.NoteIds.Contains(n.Id)) >= 20)))
            throw new DataException(ErrorCode.CapacityExceeded, "Archive capacity reached; explicitly export or remove entries first.");
        var id = Guid.NewGuid(); var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [DataKey(id)] = Encode(data, JsonStore.MaxBytes), [LibraryKey(id)] = Encode(library, LibraryStore.MaxBytes) };
        foreach (var a in library.Attachments) {
            var bytes = owner.Files.Read(AttachmentStore.Key(a.Id), AttachmentStore.MaxFileBytes);
            Require(bytes.LongLength == a.Length && Hash(bytes).Equals(a.Sha256, StringComparison.OrdinalIgnoreCase), "Attachment changed before archive capture."); payloads[BlobKey(a.Id)] = bytes;
        }
        var e = new ArchiveEntry(1, id, kind, DateTimeOffset.UtcNow, Array.AsReadOnly(data.Notes.Select(n => n.Id).ToArray()), Array.AsReadOnly(payloads.Select(p => new ArchiveReference(p.Key, p.Value.Length, Hash(p.Value))).ToArray()));
        var descriptor = Encode(e, DescriptorLimit);
        EnsureCapacity(payloads.Append(new(EntryKey(id), descriptor)));
        foreach (var p in payloads) { if (owner.Files.Exists(p.Key)) Require(Hash(owner.Files.Read(p.Key, JsonStore.MaxBytes)) == Hash(p.Value), "Archive payload identity collision."); Check(owner.Files.WriteAtomic(p.Key, p.Value, JsonStore.MaxBytes, false)); Require(Hash(owner.Files.Read(p.Key, JsonStore.MaxBytes)) == Hash(p.Value), "Archive write verification failed."); }
        Check(owner.Files.WriteAtomic(EntryKey(id), descriptor, DescriptorLimit, false)); _ = ReadArchive(id); return e;
    }
    internal void EnsureCapacity(IEnumerable<KeyValuePair<string, byte[]>> additions)
    {
        long total = 0; var replacing = additions.ToDictionary(p => p.Key, p => p.Value);
        foreach (var key in owner.Files.List("archive-")) if (!replacing.ContainsKey(key)) total += owner.Files.Read(key, JsonStore.MaxBytes).Length;
        total += replacing.Values.Sum(v => (long)v.Length);
        if (total > Math.Min(MaxTotalBytes, owner.ArchiveByteBudget) || owner.Files.List("").Count + replacing.Keys.Count(k => !owner.Files.Exists(k)) > 19998) throw new DataException(ErrorCode.CapacityExceeded, "Archive or private-file capacity exceeded.");
    }
    public Outcome PermanentlyDeleteArchive(Guid id)
    {
        try {
            owner.AssertLiveOwner(); ValidateAll();
            if (owner.Files.Exists("archive-operation.json")) return Outcome.Fail(ErrorCode.Conflict, "Resolve pending archive operation first.");
            var e = ReadDescriptor(EntryKey(id)); Check(owner.Files.Delete(EntryKey(id)));
            var retained = List().SelectMany(x => x.Files).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var r in e.Files) if (!retained.Contains(r.Key)) Check(owner.Files.Delete(r.Key));
            return Outcome.Ok();
        } catch (DataException ex) { return Outcome.Fail(ex.Code, ex.Message); }
    }
}
