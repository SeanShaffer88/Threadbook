using System.Text.Json;
using System.Text.Json.Serialization;
using Threadbook.Core;

namespace Threadbook.Storage;

public sealed class JsonStore : ISnapshotStore, IDisposable
{
    public const int MaxBytes = 25 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, MaxDepth = 32 };
    private readonly PrivateFiles provider;
    private readonly string directory;
    public string PrimaryPath => Path.Combine(directory, "threadbook.json");
    public Action? BeforeReplace { get; set; } // Explicit failure seam; never configured by normal startup.
    public IVaultFiles Files => provider;
    internal long ArchiveByteBudget { get; set; } = ArchiveStore.MaxTotalBytes;
    internal int ArchiveEntryBudget { get; set; } = ArchiveStore.MaxEntries;
    public void AssertLiveOwner() => provider.AssertLive();
    public void Lock() => provider.Lock();
    public void Unlock(ReadOnlySpan<char> password) => provider.Unlock(password);
    public static bool IsEncryptedDirectory(string directory) => PrivateFiles.Detect(directory);
    public static JsonStore CreateEncrypted(string newDirectory, ReadOnlySpan<char> password) => new(new PrivateFiles(newDirectory, true, true, password));
    public static JsonStore OpenEncrypted(string directory, ReadOnlySpan<char> password) => new(new PrivateFiles(directory, true, false, password));
    private JsonStore(PrivateFiles files) { provider = files; directory = files.RootDirectory; }
    public JsonStore(string directory)
    {
        provider = new PrivateFiles(directory, false, false, default); this.directory = provider.RootDirectory;
    }
    public Snapshot Load()
    {
        AssertLiveOwner();
        if (Files.Exists("threadbook.json")) return Parse(Files.Read("threadbook.json", MaxBytes));
        if (Backups().Count > 0) throw new DataException(ErrorCode.CorruptData, "The primary file is missing but backups exist. Recover a backup before continuing.");
        var now = DateTimeOffset.UtcNow;
        var initial = Snapshot.Empty with { Notebooks = new[] { "ideas", "projects", "reading", "personal" }.Select(n => new Notebook(Guid.NewGuid(), n, now)).ToArray() };
        var saved = Commit(initial, -1);
        if (!saved.Success) throw new DataException(saved.Error!.Code, saved.Error.Message);
        return initial;
    }
    public static Snapshot Read(string path)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaxBytes) throw new DataException(ErrorCode.CapacityExceeded, "JSON file exceeds 25 MiB.");
            var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
            return Parse(bytes);
        }
        catch (DataException) { throw; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        { throw new DataException(ErrorCode.CorruptData, "Cannot read a valid Threadbook file. Original bytes have been preserved. " + ex.GetType().Name); }
    }
    internal static Snapshot Parse(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            CheckProperties(doc.RootElement);
            var data = JsonSerializer.Deserialize<Snapshot>(bytes, Options) ?? throw new JsonException("Missing snapshot.");
            Validation.Check(data); return data.Freeze();
        }
        catch (DataException) { throw; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        { throw new DataException(ErrorCode.CorruptData, "Cannot read a valid Threadbook file. Original bytes have been preserved. " + ex.GetType().Name); }
    }
    private static void CheckProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in element.EnumerateObject()) { if (!keys.Add(p.Name)) throw new JsonException("Duplicate JSON property."); CheckProperties(p.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) CheckProperties(child);
    }
    public Outcome Commit(Snapshot candidate, long expectedRevision)
    {
        try
        {
            AssertLiveOwner();
            Validation.Check(candidate);
            var exists = Files.Exists("threadbook.json");
            if ((exists ? Parse(Files.Read("threadbook.json", MaxBytes)).Revision : -1) != expectedRevision) return Outcome.Fail(ErrorCode.Conflict, "The file revision changed. Restart before saving.");
            if (expectedRevision >= 0 && candidate.Revision != expectedRevision + 1) return Outcome.Fail(ErrorCode.Conflict, "Invalid commit revision.");
            var bytes = ArchiveStore.Encode(candidate, MaxBytes, Options);
            if (bytes.Length > MaxBytes) return Outcome.Fail(ErrorCode.CapacityExceeded, "Data exceeds the 25 MiB limit.");
            _ = Parse(bytes);
            provider.BeforeReplace = BeforeReplace;
            try { return Files.WriteAtomic("threadbook.json", bytes, MaxBytes, true); }
            finally { provider.BeforeReplace = null; }
        }
        catch (DataException ex) { return Outcome.Fail(ex.Code, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return Outcome.Fail(ErrorCode.StorageFailure, "Could not save. Your draft is still here. Check disk space and folder access."); }
    }
    public IReadOnlyList<string> Backups() => Files.List("backup-").Select(k => Files.IsEncrypted ? k : Path.Combine(directory, k)).ToArray();
    public void Recover(string backup)
    {
        AssertLiveOwner();
        var logical = Files.IsEncrypted ? backup : Path.GetRelativePath(directory, Path.GetFullPath(backup)).Replace('\\', '/');
        if (!Files.List("backup-").Contains(logical, StringComparer.Ordinal)) throw new DataException(ErrorCode.Validation, "Select a managed canonical backup.");
        var data = Parse(Files.Read(logical, MaxBytes));
        if (Files.Exists("threadbook.json"))
        {
            var preserved = Files.PreserveStored("threadbook.json", "damaged-" + Guid.NewGuid().ToString("N") + ".json", MaxBytes + (Files.IsEncrypted ? 29 : 0));
            if (!preserved.Success) throw new DataException(preserved.Error!.Code, preserved.Error.Message);
        }
        var result = Files.WriteAtomic("threadbook.json", JsonSerializer.SerializeToUtf8Bytes(data, Options), MaxBytes, false);
        if (!result.Success) throw new DataException(result.Error!.Code, result.Error.Message);
    }
    public static void Export(Snapshot data, string path, bool overwrite)
    {
        Validation.Check(data);
        var full = Path.GetFullPath(path); var temp = full + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(data, Options);
            if (bytes.Length > MaxBytes) throw new DataException(ErrorCode.CapacityExceeded, "Export exceeds 25 MiB.");
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
            if (File.Exists(full)) { if (!overwrite) throw new IOException("Export file exists."); File.Replace(temp, full, null); }
            else File.Move(temp, full);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Dispose() => provider.Dispose();
}
