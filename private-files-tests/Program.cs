using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Threadbook.Core;
using Threadbook.Storage;
using Threadbook.Desktop;

try
{
var root = Path.Combine(Path.GetTempPath(), "threadbook-private-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int count = 0;
void Check(bool ok, string test) { if (!ok) throw new Exception(test); count++; Console.WriteLine("PASS " + test); }
void Reject(Action action, string test) { try { action(); } catch (DataException) { Check(true, test); return; } throw new Exception("Expected rejection: " + test); }
const string marker = "PRIVATE-PLANTED-MARKER-4a75ac";
try
{
    foreach (bool encryptedRetention in new[] { false, true })
    {
        string directory = Path.Combine(root, "retention-" + encryptedRetention);
        using var retention = encryptedRetention ? JsonStore.CreateEncrypted(directory, "fixture retention password") : new JsonStore(directory);
        byte[] unrelated = new byte[] { 9, 8, 7, 6 };
        foreach (string name in new[] { "import.md", "import.png" }) File.WriteAllBytes(Path.Combine(directory, name), unrelated);
        var snapshot = retention.Load();
        for (int revision = 1; revision <= 9; revision++)
        {
            var saved = retention.Commit(snapshot with { Revision = revision }, revision - 1);
            Check(saved.Success && saved.Warning is null, "canonical retention without unknown-file warnings " + encryptedRetention);
            foreach (string key in new[] { "library.json", "ui-preferences.json", "recovery-draft.json", "recovery-session.json" })
            {
                saved = retention.Files.WriteAtomic(key, Encoding.UTF8.GetBytes("fixture " + revision), 1000, true);
                Check(saved.Success && saved.Warning is null, "sidecar retention without unknown-file warnings " + key + encryptedRetention);
            }
        }
        foreach (string prefix in new[] { "backup-", "library.backup-", "ui-preferences.backup-", "recovery-draft.backup-", "recovery-session.backup-" })
            Check(Directory.GetFiles(directory, prefix + "*.json").Length == 5, "five retained backups per key " + prefix + encryptedRetention);
        foreach (string name in new[] { "import.md", "import.png" }) Check(File.ReadAllBytes(Path.Combine(directory, name)).SequenceEqual(unrelated), "unknown bytes untouched " + name + encryptedRetention);
        Reject(() => retention.Files.List(""), "strict inventory still rejects unknown files " + encryptedRetention);
        Reject(() => { using var copy = VaultCopies.CreateEncryptedCopy(retention, Path.Combine(root, "retention-copy-" + encryptedRetention), "pw"); }, "copy still rejects unknown files " + encryptedRetention);
    }
    foreach (var truncated in new[] { false, true })
    {
        using var damaged = JsonStore.CreateEncrypted(Path.Combine(root, "damage-" + truncated), "pw");
        var initial = damaged.Load();
        Check(damaged.Commit(initial with { Revision = 1 }, 0).Success, "recovery backup fixture");
        var raw = damaged.Files.ReadStored("threadbook.json", JsonStore.MaxBytes + 29);
        if (truncated) raw = raw[..5]; else raw[^1] ^= 1;
        File.WriteAllBytes(damaged.PrimaryPath, raw);
        damaged.Recover(damaged.Backups()[0]);
        Check(damaged.Load().Revision == 0, "authenticated damaged primary recovery " + truncated);
        var preserved = damaged.Files.List("damaged-").Single();
        Check(damaged.Files.Read(preserved, JsonStore.MaxBytes + 29).SequenceEqual(raw), "opaque preserved stored bytes " + truncated);
        foreach (var name in new[] { "library.json", "ui-preferences.json" })
        {
            File.WriteAllBytes(Path.Combine(damaged.Files.RootDirectory, name), raw);
            var reset = name == "library.json" ? new LibraryStore(damaged).Reset(initial) : new LayoutPreferenceStore(damaged).Reset(initial);
            Check(reset.Success, "damaged sidecar reset " + name + truncated);
            var saved = damaged.Files.List(name[..^5] + ".preserved-").Single();
            Check(damaged.Files.Read(saved, JsonStore.MaxBytes + 29).SequenceEqual(raw), "sidecar raw preservation " + name + truncated);
            var largePath = Path.Combine(damaged.Files.RootDirectory, name);
            using (var large = new FileStream(largePath, FileMode.Create)) large.SetLength(JsonStore.MaxBytes + 30L);
            var before = damaged.Files.List(name[..^5] + ".preserved-").Count;
            var refused = name == "library.json" ? new LibraryStore(damaged).Reset(initial) : new LayoutPreferenceStore(damaged).Reset(initial);
            Check(!refused.Success && new FileInfo(largePath).Length == JsonStore.MaxBytes + 30L && damaged.Files.List(name[..^5] + ".preserved-").Count == before, "oversized reset preserves source without copy " + name);
        }
    }
    var encrypted = Path.Combine(root, "encrypted");
    using (var owner = JsonStore.CreateEncrypted(encrypted, "fixture password"))
    {
        var snapshot = owner.Load();
        Check(snapshot.Notebooks.Count == 4, "encrypted initial canonical load");
        var recovery = new RecoveryStore(owner);
        Check(recovery.SaveDraft(new(1, Guid.NewGuid(), snapshot.Notebooks[0].Id, null, 0, marker, marker, "", DateTimeOffset.UtcNow)).Success, "encrypted recovery draft save");
        Check(recovery.LoadDraft()?.Body == marker, "encrypted recovery roundtrip");
        Check(recovery.SaveSession(new(1, snapshot.Notebooks[0].Id, null, 0, DateTimeOffset.UtcNow)).Success, "encrypted recovery session save");
        var preferences = new LayoutPreferenceStore(owner);
        Check(preferences.Save(LayoutPreferenceState.Default, snapshot).Success, "encrypted preference save");
        Check(!preferences.Load(snapshot).PreservedInvalid, "encrypted preference load");
        var keys = new[] { "library.json", "recovery-draft.json", "recovery-session.json", "ui-preferences.json", "ui-preferences.preserved-abc.json", ".pending-" + Guid.NewGuid().ToString("N") + ".json", "attachments/" + Guid.NewGuid().ToString("N") + ".blob" };
        foreach (var key in keys) Check(owner.Files.WriteAtomic(key, Encoding.UTF8.GetBytes(marker), 1000, false).Success, "encrypted write " + key);
        var now = DateTimeOffset.UtcNow;
        var withSecret = snapshot with { Revision = 1, Notes = new[] { new Note(Guid.NewGuid(), snapshot.Notebooks[0].Id, marker, marker, "", false, now, now) } };
        Check(owner.Commit(withSecret, 0).Success, "encrypted canonical commit with backup");
        Check(owner.Commit(withSecret with { Revision = 2 }, 1).Success, "encrypted canonical secret backup");
        Check(owner.Backups().Count == 2, "encrypted backup listing");
        owner.Recover(owner.Backups()[^1]);
        Check(owner.Load().Revision == 0, "encrypted backup recover binds logical key");
        foreach (var file in Directory.GetFiles(encrypted, "*", SearchOption.AllDirectories))
            if (Path.GetFileName(file) != ".threadbook.lock")
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(marker, StringComparison.Ordinal), "no planted plaintext " + Path.GetFileName(file));
        Reject(() => owner.Files.Read("../escape", 1000), "traversal rejected");
        Reject(() => owner.Files.Read("library.json", 2), "bounded encrypted read");
        var provider = owner.Files;
        var keyField = provider.GetType().GetField("key", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var ownedKey = (byte[])keyField.GetValue(provider)!;
        owner.Lock();
        Check(ownedKey.All(b => b == 0), "lock zeroes owned key bytes");
        Reject(() => provider.Read("library.json", 1000), "locked read blocked");
        Check(!recovery.DiscardDraft().Success, "locked sidecar mutation blocked");
        Reject(() => owner.Unlock("wrong"), "wrong password rejected");
        Check(!provider.IsUnlocked, "wrong password stays locked");
        owner.Unlock("fixture password");
        ownedKey = (byte[])keyField.GetValue(provider)!;
        Check(Encoding.UTF8.GetString(provider.Read("library.json", 1000)) == marker, "unlock restores authenticated reads");
        using (var other = JsonStore.CreateEncrypted(Path.Combine(root, "other-vault"), "fixture password"))
        {
            Check(other.Files.WriteAtomic("library.json", Encoding.UTF8.GetBytes(marker), 1000, false).Success, "second vault fixture");
            File.Copy(Path.Combine(encrypted, "library.json"), Path.Combine(other.Files.RootDirectory, "library.json"), true);
            Reject(() => other.Files.Read("library.json", 1000), "cross vault transplant rejected");
        }
        File.Copy(Path.Combine(encrypted, "library.json"), Path.Combine(encrypted, "recovery-session.json"), true);
        Reject(() => provider.Read("recovery-session.json", 1000), "cross key transplant rejected");
        var stored = File.ReadAllBytes(Path.Combine(encrypted, "library.json")); stored[^1] ^= 1;
        File.WriteAllBytes(Path.Combine(encrypted, "library.json"), stored);
        Reject(() => provider.Read("library.json", 1000), "tamper rejected");
        File.WriteAllBytes(Path.Combine(encrypted, "library.json"), new byte[5]);
        Reject(() => provider.Read("library.json", 1000), "truncated encrypted file rejected");
        owner.Dispose();
        Check(ownedKey.All(b => b == 0), "dispose zeroes owned key bytes");
        Reject(() => owner.AssertLiveOwner(), "disposed owner rejected");
        Check(!provider.WriteAtomic("library.json", new byte[1], 1000, false).Success, "disposed mutation blocked");
    }
    Reject(() => { using var plain = new JsonStore(encrypted); }, "encrypted detection has no plain fallback");
    using (var plain = new JsonStore(Path.Combine(root, "plain")))
    {
        var initial = plain.Load();
        var legacyRaw = Encoding.UTF8.GetBytes("invalid " + marker);
        File.WriteAllBytes(Path.Combine(plain.Files.RootDirectory, "library.json"), legacyRaw);
        Check(new LibraryStore(plain).Reset(initial).Success, "legacy plain reset succeeds");
        var legacyPreserved = plain.Files.List("library.preserved-").Single();
        Check(plain.Files.ReadStored(legacyPreserved, 1000).SequenceEqual(legacyRaw), "legacy preservation retains exact plain bytes");
        foreach (var name in new[] { "library.json", "ui-preferences.json", "recovery-session.json", "recovery-draft.json", "attachments/" + Guid.NewGuid().ToString("N") + ".blob" })
            Check(plain.Files.WriteAtomic(name, Encoding.UTF8.GetBytes(marker), 1000, false).Success, "plain managed fixture");
        var original = plain.Files.List("").ToDictionary(k => k, k => plain.Files.ReadStored(k, JsonStore.MaxBytes));
        using var copy = VaultCopies.CreateEncryptedCopy(plain, Path.Combine(root, "copy"), "copy password");
        Check(copy.Files.IsEncrypted && copy.Load().Revision == initial.Revision, "verified new encrypted copy");
        foreach (var pair in original)
        {
            Check(pair.Value.SequenceEqual(copy.Files.Read(pair.Key, JsonStore.MaxBytes)), "copy byte equivalence " + pair.Key);
            Check(pair.Value.SequenceEqual(plain.Files.ReadStored(pair.Key, JsonStore.MaxBytes)), "source unchanged " + pair.Key);
        }
        File.WriteAllText(Path.Combine(plain.Files.RootDirectory, "unknown.secret"), marker);
        Reject(() => { using var bad = VaultCopies.CreateEncryptedCopy(plain, Path.Combine(root, "badcopy"), "pw"); }, "unknown private class copy rejected");
        Check(!Directory.Exists(Path.Combine(root, "badcopy")), "failed copy leaves no destination");
        File.Delete(Path.Combine(plain.Files.RootDirectory, "unknown.secret"));
        var hugePath = Path.Combine(plain.Files.RootDirectory, "attachments", Guid.NewGuid().ToString("N") + ".blob");
        using (var huge = File.Create(hugePath)) huge.SetLength(JsonStore.MaxBytes + 1);
        Reject(() => { using var failed = VaultCopies.CreateEncryptedCopy(plain, Path.Combine(root, "failedcopy"), "pw"); }, "mid-copy oversized payload abort");
        Check(!Directory.Exists(Path.Combine(root, "failedcopy")), "mid-copy abort removes only owned new destination");
        foreach (var pair in original) Check(pair.Value.SequenceEqual(plain.Files.ReadStored(pair.Key, JsonStore.MaxBytes)), "copy abort preserves source " + pair.Key);
        File.Delete(hugePath);
        var bytes = File.ReadAllBytes(plain.PrimaryPath);
        plain.BeforeReplace = () => throw new IOException("fixture fault");
        Check(!plain.Commit(initial with { Revision = 1 }, 0).Success, "atomic write failure returned");
        Check(bytes.SequenceEqual(File.ReadAllBytes(plain.PrimaryPath)), "failed write preserves canonical bytes");
        Check(!Directory.GetFiles(plain.Files.RootDirectory, ".vault-pending-*").Any(), "failure cleans temporary bytes");
        plain.Dispose();
        using var nextOwner = new JsonStore(Path.Combine(root, "plain"));
        Reject(() => plain.AssertLiveOwner(), "disposed owner cannot borrow replacement owner lease");
    }
    var headerPath = Path.Combine(encrypted, "vault-header.json");
    var headerBytes = File.ReadAllBytes(headerPath);
    File.WriteAllText(headerPath, Encoding.UTF8.GetString(headerBytes).Replace("600000", "2147483647", StringComparison.Ordinal));
    var timer = System.Diagnostics.Stopwatch.StartNew();
    Reject(() => { using var invalid = JsonStore.OpenEncrypted(encrypted, "fixture password"); }, "unbounded KDF header rejected");
    Check(timer.Elapsed < TimeSpan.FromSeconds(1), "header rejects KDF cost before derivation");
    File.WriteAllBytes(headerPath, headerBytes);
    using (var reopen = JsonStore.OpenEncrypted(encrypted, "fixture password")) Check(reopen.Load().Revision == 0, "reopen authenticates header and canonical");
    Console.WriteLine($"{count} private-file checks passed.");
}
finally { Directory.Delete(root, true); }
}
catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
