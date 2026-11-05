using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Threadbook.Core;

namespace Threadbook.Storage;

public interface IVaultFiles
{
    string RootDirectory { get; }
    bool IsEncrypted { get; }
    bool IsUnlocked { get; }
    bool Exists(string key);
    byte[] Read(string key, int maxPlaintextBytes);
    Outcome WriteAtomic(string key, ReadOnlyMemory<byte> plaintext, int maxPlaintextBytes, bool retainPrevious);
    IReadOnlyList<string> List(string allowedPrefix);
    Outcome Delete(string key);
    byte[] ReadStored(string key, int maxStoredBytes);
    Outcome PreserveStored(string sourceKey, string preservedKey, int maxStoredBytes);
    byte[]? ReadCryptoHeader();
}

/// <summary>Exclusive, caller-serialized session. No background activity or plaintext temporary files.</summary>
internal sealed class PrivateFiles : IVaultFiles, IDisposable
{
    internal const string HeaderName = "vault-header.json";
    private const int Iterations = 600000;
    private readonly FileStream lease;
    private readonly byte[]? header;
    private byte[]? key;
    private bool disposed;
    private sealed record Header(int Version, Guid VaultId, string Salt, int Iterations, string Check);
    private readonly Header? metadata;
    public string RootDirectory { get; }
    public bool IsEncrypted => header is not null;
    public bool IsUnlocked => !disposed && (!IsEncrypted || key is not null);
    internal Action? BeforeReplace { get; set; }
    internal static Action<string, string>? BeforeWriteForTesting { get; set; }
    internal static Action<string, string>? BeforeDeleteForTesting { get; set; }
    internal static void GuardAncestors(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new DataException(ErrorCode.Validation, "Reparse-point vault paths are not supported.");
    }
    internal static bool Detect(string directory)
    {
        GuardAncestors(directory);
        return File.Exists(Path.Combine(Path.GetFullPath(directory), HeaderName));
    }
    internal PrivateFiles(string directory, bool encrypted, bool create, ReadOnlySpan<char> password)
    {
        RootDirectory = Path.GetFullPath(directory); GuardAncestors(RootDirectory);
        if (create && (Directory.Exists(RootDirectory) || File.Exists(RootDirectory)))
            throw new DataException(ErrorCode.Validation, "Encrypted creation requires a new directory.");
        if (!encrypted && Detect(RootDirectory)) throw new DataException(ErrorCode.Validation, "This folder is encrypted; unlock it explicitly.");
        if (encrypted && !create && !Directory.Exists(RootDirectory)) throw new DataException(ErrorCode.Validation, "Vault directory does not exist.");
        if (create && (password.IsEmpty || password.Length > 4096)) throw new DataException(ErrorCode.Validation, "Password must contain 1 to 4096 characters.");
        Directory.CreateDirectory(RootDirectory);
        var leasePath = Path.Combine(RootDirectory, ".threadbook.lock");
        if (File.Exists(leasePath) && (File.GetAttributes(leasePath) & FileAttributes.ReparsePoint) != 0)
            throw new DataException(ErrorCode.Validation, "Reparse-point lock files are not supported.");
        try { lease = new FileStream(Path.Combine(RootDirectory, ".threadbook.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new DataException(ErrorCode.Conflict, "Threadbook is already using this data folder. Close the other instance first."); }
        try
        {
            if (encrypted)
            {
                if (create)
                {
                    if (password.IsEmpty) throw new DataException(ErrorCode.Validation, "A password is required.");
                    var salt = RandomNumberGenerator.GetBytes(32);
                    key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
                    var initial = new Header(1, Guid.NewGuid(), Convert.ToBase64String(salt), Iterations, "");
                    metadata = initial;
                    var check = Seal("vault-key-check", Array.Empty<byte>());
                    metadata = initial with { Check = Convert.ToBase64String(check) };
                    header = JsonSerializer.SerializeToUtf8Bytes(metadata);
                    using var output = new FileStream(Path.Combine(RootDirectory, HeaderName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    output.Write(header); output.Flush(true);
                }
                else
                {
                    header = ReadBounded(Path.Combine(RootDirectory, HeaderName), 4096);
                    using var document = JsonDocument.Parse(header, new JsonDocumentOptions { MaxDepth = 4 });
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in document.RootElement.EnumerateObject())
                        if (!names.Add(property.Name) || property.Name is not ("Version" or "VaultId" or "Salt" or "Iterations" or "Check"))
                            throw new DataException(ErrorCode.CorruptData, "Invalid encrypted vault header properties.");
                    metadata = JsonSerializer.Deserialize<Header>(header) ?? throw new JsonException();
                    if (metadata.Version != 1 || metadata.VaultId == Guid.Empty || metadata.Iterations != Iterations ||
                        metadata.Salt is not { Length: 44 } || Convert.FromBase64String(metadata.Salt).Length != 32 || metadata.Check is not { Length: 40 })
                        throw new DataException(ErrorCode.CorruptData, "Unsupported or invalid encrypted vault header.");
                    Unlock(password);
                }
            }
        }
        catch (Exception ex)
        {
            Lock(); lease.Dispose(); disposed = true;
            if (create)
            {
                // Constructor exclusively created this directory and these two files only.
                File.Delete(Path.Combine(RootDirectory, HeaderName)); File.Delete(leasePath);
                Directory.Delete(RootDirectory, false);
            }
            if (ex is JsonException or FormatException or InvalidOperationException)
                throw new DataException(ErrorCode.CorruptData, "Invalid encrypted vault header.");
            throw;
        }
    }
    internal void AssertLive()
    {
        if (!IsUnlocked) throw new DataException(ErrorCode.StorageFailure, "The vault is locked or its owner has been disposed.");
    }
    internal void Lock() { if (key is not null) { CryptographicOperations.ZeroMemory(key); key = null; } }
    internal void Unlock(ReadOnlySpan<char> password)
    {
        if (disposed) throw new DataException(ErrorCode.StorageFailure, "The vault owner has been disposed.");
        if (!IsEncrypted || key is not null) return;
        if (password.Length > 4096) throw new DataException(ErrorCode.Validation, "Password exceeds the supported length.");
        key = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(metadata!.Salt), metadata.Iterations, HashAlgorithmName.SHA256, 32);
        try { _ = Open("vault-key-check", Convert.FromBase64String(metadata.Check)); }
        catch { Lock(); throw new DataException(ErrorCode.CorruptData, "Cannot unlock the vault: password or authentication data is invalid."); }
    }
    private byte[] Aad(string logicalKey) => Encoding.UTF8.GetBytes($"Threadbook|1|{metadata!.VaultId:D}|{metadata.Salt}|{metadata.Iterations}|{logicalKey}");
    private byte[] Seal(string logicalKey, ReadOnlySpan<byte> plain)
    {
        var result = new byte[checked(plain.Length + 29)]; result[0] = 1;
        RandomNumberGenerator.Fill(result.AsSpan(1, 12));
        using var aes = new AesGcm(key!, 16);
        aes.Encrypt(result.AsSpan(1, 12), plain, result.AsSpan(29), result.AsSpan(13, 16), Aad(logicalKey));
        return result;
    }
    private byte[] Open(string logicalKey, byte[] stored)
    {
        if (stored.Length < 29 || stored[0] != 1) throw new DataException(ErrorCode.CorruptData, "Invalid encrypted private file.");
        var plain = new byte[stored.Length - 29];
        try
        {
            using var aes = new AesGcm(key!, 16);
            aes.Decrypt(stored.AsSpan(1, 12), stored.AsSpan(29), stored.AsSpan(13, 16), plain, Aad(logicalKey));
            return plain;
        }
        catch (CryptographicException) { CryptographicOperations.ZeroMemory(plain); throw new DataException(ErrorCode.CorruptData, "Private file authentication failed; original bytes preserved."); }
    }
    internal static bool Managed(string name) => Regex.IsMatch(name,
        @"\A(threadbook\.json|library\.json|archive-operation\.json|archive-(entry|data|library|stage)-[0-9a-f]{32}\.json|archive-blob-[0-9a-f]{32}\.blob|recovery-(draft|session)\.json|ui-preferences\.json|pane-sizes\.json|attachments/[0-9a-f]{32}\.blob|(backup|damaged)-[A-Za-z0-9-]+\.json|(library|ui-preferences|recovery-draft|recovery-session)\.(preserved|backup)-[A-Za-z0-9-]+\.json|\.pending-[0-9a-f]{32}\.json|\.(recovery|ui-preferences)-pending-[0-9a-f]{32})\z", RegexOptions.CultureInvariant);
    private string Resolve(string name)
    {
        AssertLive();
        if (!Managed(name)) throw new DataException(ErrorCode.Validation, "Unknown private file key.");
        GuardAncestors(RootDirectory);
        var path = Path.Combine(RootDirectory, name.Replace('/', Path.DirectorySeparatorChar));
        GuardAncestors(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new DataException(ErrorCode.Validation, "Reparse-point private files are not supported.");
        return path;
    }
    private static byte[] ReadBounded(string path, int max)
    {
        if (max < 0 || max > 1024 * 1024 * 1024) throw new DataException(ErrorCode.Validation, "Invalid private file byte limit.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new DataException(ErrorCode.Validation, "Reparse-point files are not supported.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > max) throw new DataException(ErrorCode.CapacityExceeded, "Private file exceeds its byte limit.");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes); return bytes;
    }
    public bool Exists(string key) => File.Exists(Resolve(key));
    public byte[] Read(string key, int maxPlaintextBytes)
    {
        if (maxPlaintextBytes < 0 || maxPlaintextBytes > 1024 * 1024 * 1024 - 29)
            throw new DataException(ErrorCode.Validation, "Invalid private file byte limit.");
        var bytes = ReadStored(key, checked(maxPlaintextBytes + (IsEncrypted ? 29 : 0)));
        return IsEncrypted ? Open(key, bytes) : bytes;
    }
    public byte[] ReadStored(string key, int maxStoredBytes)
    {
        try { return ReadBounded(Resolve(key), maxStoredBytes); }
        catch (DataException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new DataException(ErrorCode.StorageFailure, "Cannot read the managed private file; original bytes preserved."); }
    }
    public byte[]? ReadCryptoHeader() { AssertLive(); return header?.ToArray(); }
    // Opaque forensic payload: never authenticate/decrypt the damaged source.
    // Encrypted vaults seal these exact stored bytes under the preservation key.
    public Outcome PreserveStored(string sourceKey, string preservedKey, int maxStoredBytes)
    {
        byte[]? raw = null;
        try
        {
            _ = Resolve(sourceKey); _ = Resolve(preservedKey);
            if (maxStoredBytes < 0 || maxStoredBytes > JsonStore.MaxBytes + 29)
                return Outcome.Fail(ErrorCode.CapacityExceeded, "Preservation exceeds the stored-byte limit.");
            if (!(preservedKey.StartsWith("damaged-", StringComparison.Ordinal) ||
                  preservedKey.StartsWith("library.preserved-", StringComparison.Ordinal) ||
                  preservedKey.StartsWith("ui-preferences.preserved-", StringComparison.Ordinal)) || Exists(preservedKey))
                return Outcome.Fail(ErrorCode.Validation, "Preservation requires a new managed preservation key.");
            raw = ReadStored(sourceKey, maxStoredBytes);
            return WriteAtomic(preservedKey, raw, maxStoredBytes, false);
        }
        catch (DataException ex) { return Outcome.Fail(ex.Code, ex.Message); }
        finally { if (raw is not null) CryptographicOperations.ZeroMemory(raw); }
    }
    public IReadOnlyList<string> List(string allowedPrefix)
    {
        AssertLive(); GuardAncestors(RootDirectory);
        if (allowedPrefix != "" && allowedPrefix != "archive-" && allowedPrefix != "archive-entry-" && allowedPrefix != "attachments/" && allowedPrefix != "backup-" && allowedPrefix != "damaged-" &&
            allowedPrefix != "library.preserved-" && allowedPrefix != "ui-preferences.preserved-" &&
            allowedPrefix != "library.backup-" && allowedPrefix != "ui-preferences.backup-" &&
            allowedPrefix != "recovery-draft.backup-" && allowedPrefix != "recovery-session.backup-" && !Managed(allowedPrefix))
            throw new DataException(ErrorCode.Validation, "Unknown private file prefix.");
        var result = new List<string>();
        void Walk(string dir)
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(dir))
            {
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new DataException(ErrorCode.Validation, "Reparse-point vault entries are not supported.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!string.Equals(Path.GetRelativePath(RootDirectory, item), "attachments", StringComparison.Ordinal))
                    {
                        if (allowedPrefix.Length == 0) throw new DataException(ErrorCode.Validation, "Unknown vault directory.");
                        continue;
                    }
                    Walk(item); continue;
                }
                var logical = Path.GetRelativePath(RootDirectory, item).Replace('\\', '/');
                if (logical is HeaderName or ".threadbook.lock") continue;
                if (!Managed(logical))
                {
                    if (allowedPrefix.Length == 0) throw new DataException(ErrorCode.Validation, "Unknown vault file class.");
                    continue;
                }
                if (logical.StartsWith(allowedPrefix, StringComparison.Ordinal)) result.Add(logical);
                if (result.Count > 20000) throw new DataException(ErrorCode.CapacityExceeded, "Too many private files.");
            }
        }
        Walk(RootDirectory); return result.OrderDescending(StringComparer.Ordinal).ToArray();
    }
    public Outcome WriteAtomic(string key, ReadOnlyMemory<byte> plaintext, int maxPlaintextBytes, bool retainPrevious)
    {
        string? temporary = null;
        try
        {
            var path = Resolve(key);
            if (maxPlaintextBytes < 0 || maxPlaintextBytes > 1024 * 1024 * 1024 || plaintext.Length > maxPlaintextBytes)
                return Outcome.Fail(ErrorCode.CapacityExceeded, "Private file exceeds its byte limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = IsEncrypted ? Seal(key, plaintext.Span) : plaintext.ToArray();
            temporary = Path.Combine(Path.GetDirectoryName(path)!, ".vault-pending-" + Guid.NewGuid().ToString("N"));
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
            BeforeWriteForTesting?.Invoke(RootDirectory, key);
            BeforeReplace?.Invoke();
            string? backup = null;
            string? backupPrefix = null;
            if (retainPrevious && File.Exists(path))
            {
                backupPrefix = key == "threadbook.json" ? "backup-" : Path.GetFileNameWithoutExtension(key) + ".backup-";
                var backupKey = backupPrefix + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N") + ".json";
                // Re-encrypt under the backup logical key: AAD forbids ciphertext transplantation.
                var prior = Read(key, maxPlaintextBytes);
                var saved = WriteAtomic(backupKey, prior, maxPlaintextBytes, false);
                CryptographicOperations.ZeroMemory(prior);
                if (!saved.Success) return saved;
                backup = backupKey;
            }
            try { if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
            catch { if (backup is not null) _ = Delete(backup); throw; }
            temporary = null;
            if (backupPrefix is not null)
                try { foreach (var old in List(backupPrefix).Skip(5)) File.Delete(Resolve(old)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DataException) { return Outcome.Ok("Saved. Older backup cleanup failed; extra backups remain."); }
            return Outcome.Ok();
        }
        catch (DataException ex) { return Outcome.Fail(ex.Code, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { return Outcome.Fail(ErrorCode.StorageFailure, "Private file write failed; prior file preserved."); }
        finally { if (temporary is not null) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    public Outcome Delete(string key)
    {
        try { BeforeDeleteForTesting?.Invoke(RootDirectory, key); File.Delete(Resolve(key)); return Outcome.Ok(); }
        catch (DataException ex) { return Outcome.Fail(ex.Code, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Outcome.Fail(ErrorCode.StorageFailure, "Private file cleanup failed."); }
    }
    public void Dispose() { if (disposed) return; Lock(); disposed = true; lease.Dispose(); }
}
