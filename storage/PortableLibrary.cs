using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Threadbook.Core;
namespace Threadbook.Storage;

public static class PortableLibrary
{
    private const long MaxExpanded=1024L*1024*1024;
    private const int MaxStored=AttachmentStore.MaxFileBytes+1024;
    private sealed record Item(string Entry,string Key,long Size,string Sha256);
    private sealed record Manifest(int Version,bool Encrypted,IReadOnlyList<Item> Files);
    private static void Require(bool ok,string message) { if(!ok) throw new DataException(ErrorCode.CorruptData,message); }
    private static bool Managed(string key) => PrivateFiles.Managed(key);
    private static void Safe(string name) {
        Require(name is { Length: >0 and <=240 } && !name.Contains('\\') && !name.Contains(':') && !name.StartsWith('/') && name.Split('/').Length<=16 && name.Split('/').All(p=>p.Length>0 && p!="." && p!=".." && p==p.Trim() && !p.Any(char.IsControl)),"Unsafe archive path.");
    }
    private static byte[] ReadEntry(ZipArchiveEntry entry,int limit,CancellationToken cancellation) {
        Require(entry.Length>=0 && entry.Length<=limit,"Archive entry exceeds size budget.");
        using var input=entry.Open(); using var output=new MemoryStream(); var buffer=new byte[81920]; int n;
        while((n=input.Read(buffer))>0) { cancellation.ThrowIfCancellationRequested(); Require(output.Length+n<=limit,"Archive entry exceeds actual byte budget."); output.Write(buffer,0,n); }
        Require(output.Length==entry.Length,"Archive entry length mismatch."); return output.ToArray();
    }
    public static void CreateBackup(JsonStore owner,Stream destination,CancellationToken cancellation=default) {
        owner.AssertLiveOwner(); new ArchiveStore(owner).ValidateAll(); var keys=owner.Files.List(""); Require(keys.Count<=19998,"Too many managed files.");
        Require(keys.Contains("threadbook.json",StringComparer.Ordinal),"Save canonical notes before backup.");
        using var zip=new ZipArchive(destination,ZipArchiveMode.Create,true); var items=new List<Item>(); long total=0;
        void Add(string key,byte[] bytes) {
            cancellation.ThrowIfCancellationRequested(); Require(bytes.Length<=MaxStored && (total+=bytes.Length)<=MaxExpanded,"Backup exceeds byte budget.");
            var entry=Guid.NewGuid().ToString("N")+".bin"; using(var output=zip.CreateEntry(entry,CompressionLevel.Fastest).Open()) output.Write(bytes);
            items.Add(new(entry,key,bytes.Length,Convert.ToHexString(SHA256.HashData(bytes))));
        }
        foreach(var key in keys.Order(StringComparer.Ordinal)) { Require(Managed(key),"Unrecognized managed file."); Add(key,owner.Files.ReadStored(key,MaxStored)); }
        var header=owner.Files.ReadCryptoHeader(); if(header is not null) Add("vault-header.json",header);
        var manifestBytes=JsonSerializer.SerializeToUtf8Bytes(new Manifest(1,owner.Files.IsEncrypted,items),LibraryStore.Options);
        Require(manifestBytes.Length<=4*1024*1024 && total+manifestBytes.Length<=MaxExpanded,"Manifest exceeds backup budget.");
        using var manifest=zip.CreateEntry("manifest.json",CompressionLevel.Fastest).Open(); manifest.Write(manifestBytes);
    }
    public static void Restore(Stream archive,string newDirectory,string? password=null,CancellationToken cancellation=default) {
        Require(archive.CanSeek,"Restore requires a seekable archive stream.");
        var destination=Path.GetFullPath(newDirectory); PrivateFiles.GuardAncestors(destination);
        Require(!Directory.Exists(destination) && !File.Exists(destination),"Restore requires a new destination.");
        using var zip=new ZipArchive(archive,ZipArchiveMode.Read,true);
        Require(zip.Entries.Count is >0 and <=20000,"Archive entry count exceeds budget.");
        var entries=new Dictionary<string,ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase); long declared=0;
        foreach(var e in zip.Entries) {
            cancellation.ThrowIfCancellationRequested(); Safe(e.FullName);
            Require(entries.TryAdd(e.FullName,e),"Duplicate archive path.");
            Require(((e.ExternalAttributes>>16)&0xF000)!=0xA000 && ((e.ExternalAttributes>>16)&0xF000)!=0x4000 && (e.ExternalAttributes&16)==0 && (e.ExternalAttributes&1024)==0,"Archive links/directories are prohibited.");
            Require(e.Length<=MaxStored && e.Length>=0 && (declared+=e.Length)<=MaxExpanded,"Archive expanded budget exceeded.");
        }
        Require(entries.TryGetValue("manifest.json",out var manifestEntry),"Missing portable manifest.");
        Manifest manifest;
        try { var bytes=ReadEntry(manifestEntry!,4*1024*1024,cancellation); using var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=32}); LibraryStore.CheckDuplicates(doc.RootElement); manifest=JsonSerializer.Deserialize<Manifest>(bytes,LibraryStore.Options) ?? throw new JsonException(); }
        catch(JsonException) { throw new DataException(ErrorCode.CorruptData,"Invalid portable manifest."); }
        Require(manifest.Version==1 && manifest.Files is not null && manifest.Files.Count==zip.Entries.Count-1,"Invalid manifest version or count.");
        var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase); var referenced=new HashSet<string>(StringComparer.OrdinalIgnoreCase); long actual=0;
        foreach(var item in manifest.Files!) {
            Require(item is not null,"Missing manifest item."); Safe(item!.Key); Safe(item.Entry);
            Require(Regex.IsMatch(item.Entry,@"\A[0-9a-f]{32}\.bin\z") && referenced.Add(item.Entry) && keys.Add(item.Key),"Invalid or duplicate manifest entry.");
            Require(Managed(item.Key) || item.Key=="vault-header.json","Unknown payload class.");
            Require(item.Size>=0 && item.Size<=MaxStored && item.Sha256 is not null && Regex.IsMatch(item.Sha256,@"\A[0-9A-Fa-f]{64}\z"),"Invalid payload declaration.");
            Require(entries.TryGetValue(item.Entry,out var e),"Missing declared payload."); var bytes=ReadEntry(e!,MaxStored,cancellation);
            Require((actual+=bytes.Length)<=MaxExpanded && bytes.LongLength==item.Size && Convert.ToHexString(SHA256.HashData(bytes)).Equals(item.Sha256,StringComparison.OrdinalIgnoreCase),"Payload hash/size mismatch.");
            if(!manifest.Encrypted && item.Key=="threadbook.json") _=JsonStore.Parse(bytes);
            if(!manifest.Encrypted && item.Key=="library.json") _=LibraryStore.Parse(bytes);
        }
        Require(keys.Contains("threadbook.json") && keys.Contains("vault-header.json")==manifest.Encrypted,"Missing canonical data or inconsistent crypto header.");
        Require(!manifest.Encrypted || password is not null,"Encrypted restore requires a password for authentication.");
        cancellation.ThrowIfCancellationRequested(); bool created=false;
        try {
            Require(!Directory.Exists(destination) && !File.Exists(destination),"Destination already exists.");
            Directory.CreateDirectory(destination); created=true;
            foreach(var item in manifest.Files) {
                cancellation.ThrowIfCancellationRequested(); var bytes=ReadEntry(entries[item.Entry],MaxStored,cancellation);
                Require(bytes.LongLength==item.Size && Convert.ToHexString(SHA256.HashData(bytes)).Equals(item.Sha256,StringComparison.OrdinalIgnoreCase),"Archive changed during restore.");
                var path=Path.Combine(destination,item.Key.Replace('/',Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None); output.Write(bytes); output.Flush(true);
            }
            using var restored=manifest.Encrypted ? JsonStore.OpenEncrypted(destination,password.AsSpan()) : new JsonStore(destination);
            var canonical=restored.Load(); new ArchiveStore(restored).ValidateAll();
            foreach(var key in restored.Files.List("")) { cancellation.ThrowIfCancellationRequested(); _=restored.Files.Read(key,MaxStored); }
            if(restored.Files.Exists("library.json")) {
                var store=new LibraryStore(restored); var metadata=store.Load(canonical); Require(!metadata.WritesBlocked,"Restored library metadata is incompatible.");
                var library=new LibraryService(store,()=>canonical); var attachments=new AttachmentStore(restored,library);
                foreach(var attachment in library.State.Attachments) { cancellation.ThrowIfCancellationRequested(); using var verified=attachments.OpenRead(attachment.Id); }
            }
        } catch(Exception ex) {
            if(created) try { Directory.Delete(destination,true); } catch(Exception cleanup) when(cleanup is IOException or UnauthorizedAccessException) { throw new DataException(ErrorCode.StorageFailure,"Restore failed; owned destination cleanup also failed: "+destination); }
            if(ex is DataException or OperationCanceledException) throw;
            throw new DataException(ErrorCode.CorruptData,"Restore failed; original archive and current vault unchanged.");
        }
    }
}
