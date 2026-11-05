using System.Text;

namespace Threadbook.Core;

public sealed record WikiLinkToken(int SourceStart, int SourceLength, Guid? TargetId, string? TargetTitle, string DisplayText);
public enum WikiResolutionKind { Resolved, Broken, Ambiguous }
public sealed record WikiResolution(WikiResolutionKind Kind, Guid? TargetId = null, int? CandidateGroupId = null);
public sealed record WikiParseResult(IReadOnlyList<WikiLinkToken> Tokens, bool IsComplete, IReadOnlyList<string> Diagnostics);

/// <summary>Pure local links. Offsets are absolute UTF-16 indices in the unchanged source.</summary>
public static class WikiLinks
{
    public const int MaxLinksPerNote = 1000;
    public static string FormatStable(Guid id, string displayTitle)
    {
        ArgumentNullException.ThrowIfNull(displayTitle);
        if (displayTitle.Contains('\n') || displayTitle.Contains('\r')) throw new ArgumentException("A link cannot cross lines.", nameof(displayTitle));
        var b = new StringBuilder("[[id:" + id.ToString("D") + "|");
        foreach (char c in displayTitle) { if (c is '\\' or '|' or '[' or ']') b.Append('\\'); b.Append(c); }
        return b.Append("]]").ToString();
    }
    internal static bool TryRead(string text, int start, int sourceStart, out WikiLinkToken? token, CancellationToken cancellationToken=default)
    {
        token = null;
        if (start+1 >= text.Length || text[start]!='[' || text[start+1]!='[') return false;
        int slashes=0; for(int j=start-1;j>=0 && text[j]=='\\';j--) slashes++;
        if ((slashes&1)!=0) return false;
        var target=new StringBuilder(); var display=new StringBuilder(); bool pipe=false;
        for(int p=start+2;p<text.Length;p++)
        {
            if((p&255)==0) cancellationToken.ThrowIfCancellationRequested();
            char c=text[p];
            if(c is '\r' or '\n' or '[') return false;
            if(c=='\\')
            {
                if(++p>=text.Length || text[p] is not ('\\' or '|' or '[' or ']')) return false;
                (pipe?display:target).Append(text[p]); continue;
            }
            if(c==']')
            {
                if(p+1>=text.Length || text[p+1]!=']') return false;
                string t=target.ToString();
                if(string.IsNullOrWhiteSpace(t)) return false;
                Guid? id=null; string? title=t;
                if(t.StartsWith("id:",StringComparison.Ordinal))
                { if(!Guid.TryParseExact(t.AsSpan(3),"D",out var parsed)) return false; id=parsed; title=null; }
                else if(pipe) return false;
                token=new(sourceStart+start,p+2-start,id,title,pipe?display.ToString():t); return true;
            }
            if(c=='|' && !pipe) { pipe=true; continue; }
            (pipe?display:target).Append(c);
        }
        return false;
    }
    public static WikiParseResult Parse(string source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); cancellationToken.ThrowIfCancellationRequested();
        if(source.Length>Markdown.MaxSourceLength) return new(Array.Empty<WikiLinkToken>(),false,Array.AsReadOnly(new[]{"Body exceeds 100,000 UTF-16 characters; links omitted."}));
        var document=Markdown.Parse(source,cancellationToken); var tokens=new List<WikiLinkToken>(); bool complete=true;
        void Collect(IReadOnlyList<MarkdownInline> spans)
        {
            foreach(var span in spans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(span.Wiki is not null) { if(tokens.Count<MaxLinksPerNote) tokens.Add(span.Wiki); else complete=false; }
                if(span.Children is not null) Collect(span.Children);
            }
        }
        foreach(var block in document.Blocks) { Collect(block.Inlines); if(block.Cells is not null) foreach(var cell in block.Cells) Collect(cell); }
        return new(tokens.AsReadOnly(),complete,complete?Array.Empty<string>():Array.AsReadOnly(new[]{"1,000 links per note limit reached; backlinks are incomplete."}));
    }
}

/// <summary>Immutable snapshot-owned index. No IO, worker threads, old-index chains or mutable collection exposure.</summary>
public sealed class WikiLinkIndex
{
    public const int MaxAggregateLinks=100000;
    public const long MaxOwnedBytes=64L*1024*1024;
    private sealed record Cached(string Body, string Title, Guid Notebook, WikiParseResult Parsed);
    private sealed record Group(int Id, IReadOnlyList<Guid> Candidates, Dictionary<Guid,Guid?> Books);
    private readonly Dictionary<Guid,Cached> cache;
    private readonly Dictionary<string,Group> titles;
    private readonly Dictionary<int,IReadOnlyList<Guid>> groups;
    private readonly Dictionary<Guid,IReadOnlyList<Guid>> backlinks;
    private readonly Dictionary<Guid,IReadOnlyList<Guid>> outgoing;
    private readonly Dictionary<string,HashSet<Guid>> titleDependents;
    private readonly Dictionary<Guid,HashSet<Guid>> idDependents;
    public long Revision { get; }
    public bool IsComplete { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public int ParsedBodyCount { get; }
    public long ParsedCharacterCount { get; }
    public long EstimatedOwnedBytes { get; }
    public int LinkCount { get; }
    public int ResolutionCount { get; }
    private WikiLinkIndex(long revision, bool complete, IReadOnlyList<string> diagnostics, int parsedBodies,long parsedChars,long bytes,int links,int resolutions,
        Dictionary<Guid,Cached> cache,Dictionary<string,Group> titles,Dictionary<int,IReadOnlyList<Guid>> groups,Dictionary<Guid,IReadOnlyList<Guid>> backlinks,
        Dictionary<Guid,IReadOnlyList<Guid>> outgoing,Dictionary<string,HashSet<Guid>> titleDependents,Dictionary<Guid,HashSet<Guid>> idDependents)
    {
        Revision=revision; IsComplete=complete; Diagnostics=diagnostics; ParsedBodyCount=parsedBodies; ParsedCharacterCount=parsedChars;
        EstimatedOwnedBytes=bytes; LinkCount=links; ResolutionCount=resolutions; this.cache=cache; this.titles=titles; this.groups=groups;
        this.backlinks=backlinks; this.outgoing=outgoing; this.titleDependents=titleDependents; this.idDependents=idDependents;
    }
    public WikiResolution Resolve(Guid sourceNotebookId,WikiLinkToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if(token.TargetId is Guid id) return cache.ContainsKey(id)?new(WikiResolutionKind.Resolved,id):new(WikiResolutionKind.Broken);
        if(token.TargetTitle is null || !titles.TryGetValue(token.TargetTitle.Trim(),out var group)) return new(WikiResolutionKind.Broken);
        if(group.Books.TryGetValue(sourceNotebookId,out var local) && local is Guid unique) return new(WikiResolutionKind.Resolved,unique);
        return group.Candidates.Count==1?new(WikiResolutionKind.Resolved,group.Candidates[0]):new(WikiResolutionKind.Ambiguous,CandidateGroupId:group.Id);
    }
    public IReadOnlyList<Guid> GetBacklinks(Guid targetId) => backlinks.GetValueOrDefault(targetId) ?? Array.Empty<Guid>();
    public IReadOnlyList<Guid> GetCandidates(int groupId,int offset,int count)
    {
        if(offset<0 || count<0) throw new ArgumentOutOfRangeException(offset<0?nameof(offset):nameof(count));
        if(!groups.TryGetValue(groupId,out var items) || offset>=items.Count || count==0) return Array.Empty<Guid>();
        // Read-only page references the shared group rather than copying candidates for each link.
        return new Page(items,offset,Math.Min(count,items.Count-offset));
    }
    private sealed class Page(IReadOnlyList<Guid> items,int offset,int count) : IReadOnlyList<Guid>
    {
        public int Count=>count;
        public Guid this[int index]=>index>=0 && index<count?items[offset+index]:throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<Guid> GetEnumerator() { for(int i=0;i<count;i++) yield return items[offset+i]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    public static WikiLinkIndex Build(Snapshot snapshot,WikiLinkIndex? previous=null,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); cancellationToken.ThrowIfCancellationRequested();
        // Compare exact meaningful inputs; a revision is never a cache key.
        bool identical=previous is not null && snapshot.Notes.Count==previous.cache.Count;
        if(identical) foreach(var n in snapshot.Notes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(!previous!.cache.TryGetValue(n.Id,out var old) || old.Body!=n.Body || old.Title!=n.Title || old.Notebook!=n.NotebookId) { identical=false; break; }
        }
        if(identical) return new(snapshot.Revision,previous!.IsComplete,previous.Diagnostics,0,0,previous.EstimatedOwnedBytes,previous.LinkCount,0,
            previous.cache,previous.titles,previous.groups,previous.backlinks,previous.outgoing,previous.titleDependents,previous.idDependents);
        var cache=new Dictionary<Guid,Cached>(); var titleMembers=new Dictionary<string,List<Guid>>(StringComparer.OrdinalIgnoreCase);
        var diagnostics=new List<string>(); bool complete=true; int bodies=0,links=0; long chars=0,bytes=4096;
        void Incomplete(string message) { complete=false; if(diagnostics.Count<8 && !diagnostics.Contains(message)) diagnostics.Add(message); }
        var changedTitles=new HashSet<string>(StringComparer.OrdinalIgnoreCase); var changedIds=new HashSet<Guid>(); var affected=new HashSet<Guid>();
        // Conservative accounting includes dictionary capacity/slack, group/book maps, retained key/body references,
        // token text (worst-case 4x UTF-16 source length), dependency sets and both edge directions.
        foreach(var n in snapshot.Notes.OrderBy(n=>n.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long baseCost=2048L+n.Title.Length*4L;
            if(bytes+baseCost>MaxOwnedBytes) { Incomplete("64 MiB owned index budget reached; membership and backlinks are incomplete."); continue; }
            bytes+=baseCost;
            Cached? old=null; previous?.cache.TryGetValue(n.Id,out old);
            WikiParseResult parsed;
            long tokenCost=0;
            if(old is not null && old.Body==n.Body && old.Parsed.IsComplete) parsed=old.Parsed;
            else { parsed=WikiLinks.Parse(n.Body,cancellationToken); bodies++; chars+=Math.Min(n.Body.Length,Markdown.MaxSourceLength); affected.Add(n.Id); }
            var admitted=new List<WikiLinkToken>();
            foreach(var token in parsed.Tokens)
            {
                long cost=512L+token.SourceLength*8L;
                if(links==MaxAggregateLinks || bytes+tokenCost+cost>MaxOwnedBytes) { Incomplete("Aggregate link or owned index budget reached; backlinks are incomplete."); break; }
                admitted.Add(token); tokenCost+=cost; links++;
            }
            bytes+=tokenCost;
            if(!parsed.IsComplete) Incomplete("Some note links were omitted by parser limits; backlinks are incomplete.");
            // Preserve full parse cache only when all tokens were admitted; otherwise reconsider on the next build.
            bool all=admitted.Count==parsed.Tokens.Count;
            if(!all) parsed=new(admitted.AsReadOnly(),false,Array.AsReadOnly(new[]{"Index admission limit reached."}));
            cache.Add(n.Id,new(n.Body,n.Title,n.NotebookId,parsed));
            string title=n.Title.Trim(); if(!titleMembers.TryGetValue(title,out var members)) titleMembers.Add(title,members=new()); members.Add(n.Id);
            if(old is null || old.Title!=n.Title || old.Notebook!=n.NotebookId)
            { changedIds.Add(n.Id); changedTitles.Add(title); if(old is not null) changedTitles.Add(old.Title.Trim()); affected.Add(n.Id); }
        }
        if(previous is not null) foreach(var pair in previous.cache) if(!cache.ContainsKey(pair.Key)) { changedIds.Add(pair.Key); changedTitles.Add(pair.Value.Title.Trim()); }
        if(previous is not null)
        {
            foreach(var title in changedTitles) if(previous.titleDependents.TryGetValue(title,out var refs)) affected.UnionWith(refs);
            foreach(var id in changedIds) if(previous.idDependents.TryGetValue(id,out var refs)) affected.UnionWith(refs);
        }
        var titles=new Dictionary<string,Group>(StringComparer.OrdinalIgnoreCase); var groups=new Dictionary<int,IReadOnlyList<Guid>>();
        // Old group handles cannot silently select a different group in a descendant index.
        int groupId=previous is null || previous.groups.Count==0?0:previous.groups.Keys.Max();
        foreach(var pair in titleMembers)
        {
            cancellationToken.ThrowIfCancellationRequested(); var books=new Dictionary<Guid,Guid?>();
            foreach(var id in pair.Value) { Guid book=cache[id].Notebook; books[book]=books.ContainsKey(book)?null:id; }
            var members=pair.Value.AsReadOnly(); groupId=checked(groupId+1); titles.Add(pair.Key,new(groupId,members,books)); groups.Add(groupId,members);
        }
        var backlinks=new Dictionary<Guid,IReadOnlyList<Guid>>(); var outgoing=new Dictionary<Guid,IReadOnlyList<Guid>>();
        var td=new Dictionary<string,HashSet<Guid>>(StringComparer.OrdinalIgnoreCase); var ids=new Dictionary<Guid,HashSet<Guid>>();
        var index=new WikiLinkIndex(snapshot.Revision,complete,diagnostics.AsReadOnly(),bodies,chars,bytes,links,0,cache,titles,groups,backlinks,outgoing,td,ids);
        var reverse=new Dictionary<Guid,HashSet<Guid>>(); int resolutions=0;
        foreach(var pair in cache)
        {
            cancellationToken.ThrowIfCancellationRequested(); var n=pair.Value;
            foreach(var token in n.Parsed.Tokens)
            {
                if(token.TargetId is Guid target) { if(!ids.TryGetValue(target,out var refs)) ids.Add(target,refs=new()); refs.Add(pair.Key); }
                else if(token.TargetTitle is string title) { title=title.Trim(); if(!td.TryGetValue(title,out var refs)) td.Add(title,refs=new()); refs.Add(pair.Key); }
            }
            IReadOnlyList<Guid> targets;
            if(previous is not null && previous.IsComplete && complete && !affected.Contains(pair.Key) && previous.outgoing.TryGetValue(pair.Key,out var oldTargets)) targets=oldTargets;
            else
            {
                var found=new HashSet<Guid>(); foreach(var token in n.Parsed.Tokens) { cancellationToken.ThrowIfCancellationRequested(); resolutions++; var r=index.Resolve(n.Notebook,token); if(r.TargetId is Guid id) found.Add(id); }
                targets=Array.AsReadOnly(found.Order().ToArray());
            }
            outgoing.Add(pair.Key,targets);
            foreach(var target in targets) { if(!reverse.TryGetValue(target,out var refs)) reverse.Add(target,refs=new()); refs.Add(pair.Key); }
        }
        foreach(var pair in reverse) backlinks.Add(pair.Key,Array.AsReadOnly(pair.Value.Order().ToArray()));
        return new(snapshot.Revision,complete,diagnostics.AsReadOnly(),bodies,chars,bytes,links,resolutions,cache,titles,groups,backlinks,outgoing,td,ids);
    }
}
