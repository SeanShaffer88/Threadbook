using System.Security.Cryptography;
using System.Buffers.Binary;
using Threadbook.Core;
namespace Threadbook.Storage;
public sealed record AttachmentStage(AttachmentInfo Info);
public sealed class AttachmentStore(JsonStore owner, LibraryService library)
{
    public const int MaxFileBytes=25*1024*1024;
    public const long MaxTotalBytes=512L*1024*1024;
    private readonly Dictionary<Guid,AttachmentStage> stages=new();
    internal static string Key(Guid id)=>"attachments/"+id.ToString("N")+".blob";
    public AttachmentStage Stage(Guid noteId,Stream input,string displayName,string mediaType,CancellationToken cancellation=default) {
        owner.AssertLiveOwner();
        if(noteId==Guid.Empty || string.IsNullOrWhiteSpace(displayName) || displayName.Length>255 || displayName.IndexOfAny(['/', '\\', ':', '\0'])>=0 || displayName.Any(char.IsControl) || string.IsNullOrWhiteSpace(mediaType) || mediaType.Length>100 || mediaType.Any(char.IsControl)) throw new DataException(ErrorCode.Validation,"Invalid attachment metadata.");
        if(library.WritesBlocked) throw new DataException(ErrorCode.CorruptData,"Library writes are blocked.");
        using var buffer=new MemoryStream(); var chunk=new byte[81920]; int count;
        while((count=input.Read(chunk))>0) { cancellation.ThrowIfCancellationRequested(); if(buffer.Length+count>MaxFileBytes) throw new DataException(ErrorCode.CapacityExceeded,"Attachment exceeds 25 MiB."); buffer.Write(chunk,0,count); }
        cancellation.ThrowIfCancellationRequested();
        if(library.State.Attachments.Count+stages.Count>=10000 || library.State.Attachments.Sum(a=>a.Length)+stages.Values.Sum(a=>a.Info.Length)+buffer.Length>MaxTotalBytes) throw new DataException(ErrorCode.CapacityExceeded,"Attachment library limit exceeded.");
        var bytes=buffer.ToArray(); ValidateImageHeader(bytes,mediaType); var info=new AttachmentInfo(Guid.NewGuid(),noteId,displayName,mediaType,bytes.Length,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var saved=owner.Files.WriteAtomic(Key(info.Id),bytes,MaxFileBytes,false); if(!saved.Success) throw new DataException(saved.Error!.Code,saved.Error.Message);
        var stage=new AttachmentStage(info); stages.Add(info.Id,stage); return stage;
    }
    // Header checks bound preview dimensions. A UI decoder must still reject malformed image content.
    private static void ValidateImageHeader(byte[] bytes,string mediaType) {
        void Dimensions(uint width,uint height) { if(width==0 || height==0 || (ulong)width*height>16000000) throw new DataException(ErrorCode.CapacityExceeded,"Image exceeds the 16 megapixel preview budget."); }
        if(mediaType.Equals("image/png",StringComparison.OrdinalIgnoreCase)) {
            if(bytes.Length<33 || !bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}) || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8,4))!=13 || !bytes.AsSpan(12,4).SequenceEqual("IHDR"u8)) throw new DataException(ErrorCode.CorruptData,"Invalid PNG image header.");
            Dimensions(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16,4)),BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20,4)));
        } else if(mediaType.Equals("image/jpeg",StringComparison.OrdinalIgnoreCase)) {
            if(bytes.Length<4 || bytes[0]!=255 || bytes[1]!=216) throw new DataException(ErrorCode.CorruptData,"Invalid JPEG image header.");
            int offset=2;
            while(offset<bytes.Length) {
                if(bytes[offset++]!=255) break;
                while(offset<bytes.Length && bytes[offset]==255) offset++;
                if(offset>=bytes.Length) break; var marker=bytes[offset++];
                if(marker is 0xD9 or 0xDA) break;
                if(marker is 0x01 or >=0xD0 and <=0xD7) continue;
                if(offset+2>bytes.Length) break; var length=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset,2));
                if(length<2 || offset+length>bytes.Length) break;
                if(marker is >=0xC0 and <=0xC3 or >=0xC5 and <=0xC7 or >=0xC9 and <=0xCB or >=0xCD and <=0xCF) {
                    if(length<8) break; Dimensions(BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset+5,2)),BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset+3,2))); return;
                }
                offset+=length;
            }
            throw new DataException(ErrorCode.CorruptData,"Missing bounded JPEG dimensions.");
        }
    }
    public Stream OpenRead(Guid id,Guid noteId) {
        if(!library.State.Attachments.Any(a=>a.Id==id && a.NoteId==noteId)) throw new DataException(ErrorCode.NotFound,"Attachment does not belong to this note.");
        return OpenRead(id);
    }
    public Outcome AddToNote(AttachmentStage stage) {
        if(!stages.TryGetValue(stage.Info.Id,out var owned) || owned!=stage) return Outcome.Fail(ErrorCode.Validation,"Unknown attachment stage.");
        var result=library.Execute(new AddLibraryAttachment(stage.Info));
        if(result.Success) { stages.Remove(stage.Info.Id); return result; }
        var cleanup=Discard(stage); return result with { Warning=cleanup.Success ? result.Warning : "Metadata was not saved; staged blob cleanup failed." };
    }
    public Outcome Discard(AttachmentStage stage) {
        if(!stages.TryGetValue(stage.Info.Id,out var owned) || owned!=stage) return Outcome.Fail(ErrorCode.Validation,"Unknown attachment stage.");
        var result=owner.Files.Delete(Key(stage.Info.Id)); if(result.Success) stages.Remove(stage.Info.Id); return result;
    }
    public Stream OpenRead(Guid id) {
        owner.AssertLiveOwner(); var info=library.State.Attachments.SingleOrDefault(a=>a.Id==id) ?? throw new DataException(ErrorCode.NotFound,"Attachment not found.");
        var bytes=owner.Files.Read(Key(id),MaxFileBytes);
        if(bytes.LongLength!=info.Length || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(info.Sha256,StringComparison.OrdinalIgnoreCase)) throw new DataException(ErrorCode.CorruptData,"Attachment integrity check failed.");
        return new MemoryStream(bytes,false);
    }
    public Outcome Remove(Guid id) {
        var result=library.Execute(new RemoveLibraryAttachment(id)); if(!result.Success) return result;
        var removed=owner.Files.Delete(Key(id)); return removed.Success ? result : Outcome.Ok("Attachment metadata removed; orphan blob cleanup failed.");
    }
}
