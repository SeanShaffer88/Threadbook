using System.Text;
using System.Text.Json;
using Threadbook.Core;
namespace Threadbook.Storage;
public static class NoteImports
{
    internal static IReadOnlyList<string> CaptureEntries(IEnumerable<string> entries,int remaining,CancellationToken cancellation) {
        var captured=new List<string>();
        using var enumerator=entries.GetEnumerator();
        while(true) {
            cancellation.ThrowIfCancellationRequested();
            if(!enumerator.MoveNext()) break;
            cancellation.ThrowIfCancellationRequested();
            if(captured.Count>=remaining) throw new DataException(ErrorCode.CapacityExceeded,"Import scan exceeds 10,000 filesystem entries.");
            captured.Add(enumerator.Current);
        }
        captured.Sort(StringComparer.Ordinal); return captured;
    }
    public static Snapshot PrepareMarkdownImport(Snapshot current,IEnumerable<string> paths,Guid targetNotebook,CancellationToken cancellation=default) {
        Validation.Check(current); if(!current.Notebooks.Any(b=>b.Id==targetNotebook)) throw new DataException(ErrorCode.NotFound,"Select an existing notebook.");
        int visited=0; var files=new List<string>(); var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(string input,int depth,bool counted=false) {
            cancellation.ThrowIfCancellationRequested(); if(!counted && ++visited>10000) throw new DataException(ErrorCode.CapacityExceeded,"Import scan exceeds 10,000 filesystem entries."); if(depth>16) throw new DataException(ErrorCode.CapacityExceeded,"Import folder depth exceeds 16.");
            var path=Path.GetFullPath(input); PrivateFiles.GuardAncestors(Path.GetDirectoryName(path)!);
            var attributes=File.GetAttributes(path); if((attributes&FileAttributes.ReparsePoint)!=0) throw new DataException(ErrorCode.Validation,"Import refuses reparse points.");
            if((attributes&FileAttributes.Directory)!=0) {
                var children=CaptureEntries(Directory.EnumerateFileSystemEntries(path),10000-visited,cancellation);
                visited+=children.Count;
                foreach(var child in children) Walk(child,depth+1,true);
            }
            else if(Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" or ".txt") { if(seen.Add(path)) files.Add(path); if(files.Count>1000) throw new DataException(ErrorCode.CapacityExceeded,"Import exceeds 1000 files."); }
        }
        foreach(var path in paths) Walk(path,0);
        var notes=current.Notes.ToList(); long total=0;
        foreach(var path in files) {
            cancellation.ThrowIfCancellationRequested(); using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read); using var output=new MemoryStream(); var buffer=new byte[8192]; int n;
            while((n=input.Read(buffer))>0) { cancellation.ThrowIfCancellationRequested(); if(output.Length+n>400003 || (total+=n)>JsonStore.MaxBytes) throw new DataException(ErrorCode.CapacityExceeded,"Import byte limit exceeded."); output.Write(buffer,0,n); }
            var bytes=output.ToArray(); var offset=bytes.AsSpan().StartsWith(new byte[]{239,187,191}) ? 3 : 0;
            string body; try { body=new UTF8Encoding(false,true).GetString(bytes,offset,bytes.Length-offset); } catch(DecoderFallbackException) { throw new DataException(ErrorCode.CorruptData,"Import requires valid UTF-8."); }
            if(body.Length>100000) throw new DataException(ErrorCode.CapacityExceeded,"Import note exceeds 100,000 characters.");
            var title=Path.GetFileNameWithoutExtension(path); if(string.IsNullOrWhiteSpace(title)) title="Imported note"; if(title.Length>200) title=title[..200]; var now=DateTimeOffset.UtcNow;
            notes.Add(new(Guid.NewGuid(),targetNotebook,title,body,"",false,now,now));
        }
        cancellation.ThrowIfCancellationRequested(); return Finish(current with { Revision=checked(current.Revision+1),Notes=notes });
    }
    public static Snapshot PrepareJsonAppend(Snapshot incoming,Snapshot current) {
        Validation.Check(incoming); Validation.Check(current); var ids=new Dictionary<Guid,Guid>();
        foreach(var id in incoming.Notebooks.Select(b=>b.Id).Concat(incoming.Notes.Select(n=>n.Id)).Concat(incoming.Replies.Select(r=>r.Id))) ids.Add(id,Guid.NewGuid());
        var names=current.Notebooks.Select(b=>b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase); var books=current.Notebooks.ToList();
        foreach(var b in incoming.Notebooks) { var name=b.Name; var suffix=1; while(!names.Add(name)) { var tail=" ("+(suffix++)+")"; name=b.Name[..Math.Min(b.Name.Length,60-tail.Length)]+tail; } books.Add(b with {Id=ids[b.Id],Name=name}); }
        return Finish(current with { Revision=checked(current.Revision+1),Notebooks=books,Notes=current.Notes.Concat(incoming.Notes.Select(n=>n with { Id=ids[n.Id],NotebookId=ids[n.NotebookId] })).ToArray(),Replies=current.Replies.Concat(incoming.Replies.Select(r=>r with {Id=ids[r.Id],NoteId=ids[r.NoteId],ParentId=r.ParentId is Guid p ? ids[p] : null})).ToArray() });
    }
    private static Snapshot Finish(Snapshot snapshot) { Validation.Check(snapshot); if(JsonSerializer.SerializeToUtf8Bytes(snapshot,new JsonSerializerOptions { WriteIndented=true }).Length>JsonStore.MaxBytes) throw new DataException(ErrorCode.CapacityExceeded,"Combined snapshot exceeds 25 MiB."); return snapshot.Freeze(); }
}
