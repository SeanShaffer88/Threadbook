using System.Security.Cryptography;
using Threadbook.Core;

namespace Threadbook.Storage;

public static class VaultCopies
{
    /// <summary>Create and verify a NEW encrypted copy. Original plaintext and backups remain unchanged.</summary>
    public static JsonStore CreateEncryptedCopy(JsonStore source, string newDirectory, ReadOnlySpan<char> password)
    {
        source.AssertLiveOwner(); new ArchiveStore(source).ValidateAll();
        var destination = Path.GetFullPath(newDirectory);
        PrivateFiles.GuardAncestors(destination);
        var sourceRoot = Path.TrimEndingDirectorySeparator(source.Files.RootDirectory);
        if (destination.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(destination, sourceRoot, StringComparison.OrdinalIgnoreCase))
            throw new DataException(ErrorCode.Validation, "Copy destination must be outside the source vault.");
        if (Directory.Exists(destination) || File.Exists(destination)) throw new DataException(ErrorCode.Validation, "Copy destination must be new.");
        var keys = source.Files.List(""); // Reject unknown classes before creating anything.
        if (!keys.Contains("threadbook.json")) throw new DataException(ErrorCode.Validation, "Source must contain canonical notes.");
        _ = JsonStore.Parse(source.Files.Read("threadbook.json", JsonStore.MaxBytes));
        JsonStore? target = null;
        bool owned = false;
        try
        {
            target = JsonStore.CreateEncrypted(destination, password); owned = true;
            long total = 0;
            foreach (var key in keys)
            {
                var bytes = source.Files.Read(key, JsonStore.MaxBytes);
                total = checked(total + bytes.Length);
                if (total > 1024L * 1024 * 1024) throw new DataException(ErrorCode.CapacityExceeded, "Copy exceeds 1 GiB.");
                try
                {
                    var saved = target.Files.WriteAtomic(key, bytes, JsonStore.MaxBytes, false);
                    if (!saved.Success) throw new DataException(saved.Error!.Code, saved.Error.Message);
                    var verified = target.Files.Read(key, JsonStore.MaxBytes);
                    try { if (!CryptographicOperations.FixedTimeEquals(bytes, verified)) throw new DataException(ErrorCode.CorruptData, "Encrypted copy verification failed."); }
                    finally { CryptographicOperations.ZeroMemory(verified); }
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            _ = target.Load(); new ArchiveStore(target).ValidateAll();
            return target;
        }
        catch
        {
            target?.Dispose();
            if (owned)
            {
                // Never recursively delete an unchecked path or follow a substituted reparse entry.
                PrivateFiles.GuardAncestors(destination);
                var files = new List<string>();
                var directories = new List<string>();
                void Inspect(string current)
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            throw new DataException(ErrorCode.StorageFailure, "Copy cleanup requires manual review of a reparse entry.");
                        if ((attributes & FileAttributes.Directory) != 0) { Inspect(entry); directories.Add(entry); }
                        else files.Add(entry);
                        if (files.Count + directories.Count > 20002) throw new DataException(ErrorCode.StorageFailure, "Copy cleanup exceeded its entry bound.");
                    }
                }
                Inspect(destination);
                foreach (var file in files) File.Delete(file);
                foreach (var dir in directories) Directory.Delete(dir, false);
                Directory.Delete(destination, false);
            }
            throw;
        }
    }
}
