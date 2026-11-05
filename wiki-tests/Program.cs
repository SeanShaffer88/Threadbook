using Threadbook.Core;

try
{
    int passed=0;
    void Check(bool value,string message="Assertion failed") { if(!value) throw new Exception(message); }
    void Test(string name,Action action) { action(); passed++; Console.WriteLine("PASS "+name); }
    Guid book=Guid.NewGuid(), other=Guid.NewGuid(); DateTimeOffset now=DateTimeOffset.UtcNow;
    Note N(string title,string body="",Guid? notebook=null,Guid? id=null)=>new(id??Guid.NewGuid(),notebook??book,title,body,"",false,now,now);
    Snapshot S(params Note[] notes)=>new(1,1,new[]{new Notebook(book,"A",now),new Notebook(other,"B",now)},notes,Array.Empty<Reply>());
    WikiLinkToken T(string title)=>WikiLinks.Parse("[["+title+"]]").Tokens.Single();
    Test("stable format escapes decoded exactly once",()=>{
        Guid id=Guid.NewGuid(); string display="\\|[] **literal** 😀"; string raw=WikiLinks.FormatStable(id,display);
        var t=WikiLinks.Parse(raw).Tokens.Single(); Check(t.TargetId==id && t.DisplayText==display && t.SourceLength==raw.Length);
        Check(Markdown.Parse(raw).Blocks[0].Inlines.Single().Children is null);
    });
    Test("unicode UTF16 absolute offsets CRLF headings",()=>{
        string source="😀\r\n# é [[標題]]"; var t=WikiLinks.Parse(source).Tokens.Single();
        Check(t.SourceStart==source.IndexOf("[[",StringComparison.Ordinal) && source.Substring(t.SourceStart,t.SourceLength)=="[[標題]]");
    });
    Test("shared exclusions and escaped open",()=>{
        foreach(string s in new[]{"`[[hidden]]`","`[[a`]]","```\n[[hidden]]\n```","~~~\n[[hidden]]","\\[[hidden]]","[a [[hidden]]](local)","![a [[hidden]]](local)","[a](https://x/[[hidden]])","https://x/[[hidden]]","<https://x/[[hidden]]>","www.x/[[hidden]]","ftp://x/[[hidden]]","mailto:x/[[hidden]]","file:x/[[hidden]]"}) Check(WikiLinks.Parse(s).Tokens.Count==0,s);
        Check(WikiLinks.Parse("\\\\[[yes]]").Tokens.Count==1);
    });
    Test("malformed literal no exceptions",()=>{
        foreach(string s in new[]{"[[", "[[bad", "[[id:no|label]]", "[[ ]]", "[[a\\q]]", "[[a\nb]]", "[[a|b]]", "[[a]b]]"}) {
            Check(WikiLinks.Parse(s).Tokens.Count==0,s); Check(string.Concat(Markdown.ParseInline(s).Select(x=>x.Text))==s,s);
        }
    });
    Test("styles children and display not markdown",()=>{
        foreach(string style in new[]{"**","*","~~","__","_"}) {
            string raw=style+WikiLinks.FormatStable(Guid.NewGuid(),"**shown**|[]")+style;
            var t=WikiLinks.Parse(raw).Tokens.Single(); var span=Markdown.ParseInline(raw).Single();
            Check(span.Children!.Single().Wiki==t && t.SourceStart==style.Length && t.DisplayText=="**shown**|[]");
        }
    });
    Test("wiki table pipes and escaped pipes",()=>{
        string raw="| [[title]] | "+WikiLinks.FormatStable(Guid.NewGuid(),"a|b\\c[]")+" |\n|---|---|\n| c | d |";
        var d=Markdown.Parse(raw); Check(d.Blocks.Single().Columns==2 && d.Blocks[0].Cells!.Count==4); Check(WikiLinks.Parse(raw).Tokens.Count==2);
    });
    Test("passive HTML preserves raw source syntax",()=>{
        string s=WikiLinks.FormatStable(Guid.NewGuid(),"<script> & **x**"); string html=Markdown.ExportHtml("x",s);
        Check(html.Contains("[[id:") && html.Contains("&lt;script&gt;") && !html.Contains("href=") && !html.Contains("<script>"));
    });
    Test("deep nested malformed source bounded",()=>{
        string raw=string.Concat(Enumerable.Repeat("**~~_",5000))+"[[x]]"+string.Concat(Enumerable.Repeat("_~~**",5000));
        var p=WikiLinks.Parse(raw); Check(p.IsComplete); Check(raw.Length<100000);
    });
    Test("local unique versus global ambiguous and candidate pages",()=>{
        Note a=N("Topic"),b=N("topic",notebook:other),c=N("TOPIC",notebook:other); var i=WikiLinkIndex.Build(S(a,b,c));
        Check(i.Resolve(book,T(" topic ")).TargetId==a.Id);
        var r=i.Resolve(other,T("topic")); Check(r.Kind==WikiResolutionKind.Ambiguous);
        Check(i.GetCandidates(r.CandidateGroupId!.Value,1,1).SequenceEqual(new[]{b.Id,c.Id,a.Id}.Order().Skip(1).Take(1)));
        Check(i.GetCandidates(r.CandidateGroupId.Value,99,1).Count==0);
    });
    Test("stable rename delete revival and unique backlinks",()=>{
        var target=N("Old"); var source=N("Source",WikiLinks.FormatStable(target.Id,"Old")+" "+WikiLinks.FormatStable(target.Id,"Old"));
        var i=WikiLinkIndex.Build(S(target,source)); Check(i.GetBacklinks(target.Id).SequenceEqual(new[]{source.Id}));
        var renamed=target with {Title="New"}; var j=WikiLinkIndex.Build(S(renamed,source),i);
        Check(j.GetBacklinks(target.Id).Count==1 && j.ParsedBodyCount==0);
        var k=WikiLinkIndex.Build(S(source),j); Check(k.GetBacklinks(target.Id).Count==0 && k.Resolve(book,WikiLinks.Parse(source.Body).Tokens[0]).Kind==WikiResolutionKind.Broken);
        var l=WikiLinkIndex.Build(S(renamed,source),k); Check(l.GetBacklinks(target.Id).Count==1 && source.Body.Contains("Old"));
    });
    Test("title rename moves duplicate and old groups remain immutable",()=>{
        Note a=N("X"),b=N("X",notebook:other),src=N("S","[[X]]"); var i=WikiLinkIndex.Build(S(a,b,src));
        Check(i.GetBacklinks(a.Id).Count==1); var moved=a with {NotebookId=other}; var j=WikiLinkIndex.Build(S(moved,b,src),i);
        var r=j.Resolve(book,T("X")); Check(r.Kind==WikiResolutionKind.Ambiguous); var page=j.GetCandidates(r.CandidateGroupId!.Value,0,10);
        var k=WikiLinkIndex.Build(S(moved with {Title="Z"},b,src),j); Check(k.GetBacklinks(b.Id).Count==1 && page.Count==2 && j.GetBacklinks(a.Id).Count==0);
        Check(k.GetCandidates(r.CandidateGroupId.Value,0,10).Count==0,"Stale group reused");
        Check(i.GetBacklinks(a.Id).Count==1 && src.Body=="[[X]]");
    });
    Test("exact body cache and same revision snapshot replacement",()=>{
        Note a=N("A"),src=N("S","[[A]]"); var i=WikiLinkIndex.Build(S(a,src));
        var warm=WikiLinkIndex.Build(S(a,src with {Saved=true,Flair="x",Updated=now.AddSeconds(1)}) with {Replies=new[]{new Reply(Guid.NewGuid(),src.Id,null,"reply",now,now)}},i);
        Check(warm.ParsedBodyCount==0 && warm.ParsedCharacterCount==0 && warm.ResolutionCount==0);
        var edit=WikiLinkIndex.Build(S(a,src with {Body="[[missing]]"}),warm);
        Check(edit.ParsedBodyCount==1 && edit.ParsedCharacterCount=="[[missing]]".Length && edit.GetBacklinks(a.Id).Count==0);
    });
    Test("collections cannot mutate exposed state",()=>{
        var p=WikiLinks.Parse("[[A]]"); Check(p.Tokens is not WikiLinkToken[]);
        try { ((IList<WikiLinkToken>)p.Tokens).Clear(); throw new Exception("Mutable tokens"); } catch(NotSupportedException) { }
        Note a=N("A"),s=N("S","[[A]]"); var i=WikiLinkIndex.Build(S(a,s));
        try { ((IList<Guid>)i.GetBacklinks(a.Id)).Clear(); throw new Exception("Mutable backlinks"); } catch(NotSupportedException) { }
    });
    Test("body and per-note bounds honest completeness",()=>{
        string source=string.Concat(Enumerable.Repeat("[[A]]",1001)); var p=WikiLinks.Parse(source);
        Check(!p.IsComplete && p.Tokens.Count==1000 && p.Diagnostics.Count>0 && source.Length==5005);
        var oversized=WikiLinks.Parse(new string('x',100001)); Check(!oversized.IsComplete && oversized.Tokens.Count==0);
        var i=WikiLinkIndex.Build(S(N("A"),N("S",source))); Check(!i.IsComplete && i.Diagnostics.Count>0 && i.LinkCount==1000);
    });
    Test("aggregate and owned budget bounded and previous immutable",()=>{
        var notes=Enumerable.Range(0,110).Select(n=>N("N"+n,string.Concat(Enumerable.Repeat("[[A]]",1000)))).ToArray();
        var i=WikiLinkIndex.Build(S(notes)); Check(!i.IsComplete && i.EstimatedOwnedBytes<=WikiLinkIndex.MaxOwnedBytes && i.LinkCount==100000 && i.Diagnostics.Count>0);
        var j=WikiLinkIndex.Build(S(N("A"),N("S","[[A]]")),i); Check(j.IsComplete && j.LinkCount==1 && !i.IsComplete);
    });
    Test("owned byte budget and omitted entries reconsidered",()=>{
        string link="[["+new string('t',1000)+"]]";
        var notes=Enumerable.Range(0,90).Select(n=>N("N"+n,string.Concat(Enumerable.Repeat(link,99)))).ToArray();
        var i=WikiLinkIndex.Build(S(notes)); Check(!i.IsComplete && i.EstimatedOwnedBytes<=WikiLinkIndex.MaxOwnedBytes && i.LinkCount<90*99);
        var last=notes.OrderBy(n=>n.Id).Last(); var j=WikiLinkIndex.Build(S(last),i);
        Check(j.IsComplete && j.LinkCount==99,"Previously omitted tokens never reconsidered");
    });
    Test("body edit leaves unrelated resolution dependencies untouched",()=>{
        Note a=N("A"),b=N("B"),srcA=N("SA","[[A]]"),srcB=N("SB","[[B]]");
        var i=WikiLinkIndex.Build(S(a,b,srcA,srcB)); var j=WikiLinkIndex.Build(S(a with {Title="C"},b,srcA,srcB),i);
        Check(j.ParsedBodyCount==0 && j.ResolutionCount==1 && j.GetBacklinks(a.Id).Count==0 && j.GetBacklinks(b.Id).Count==1);
    });
    Test("cancellation throws preserves prior result",()=>{
        Note a=N("A"); var i=WikiLinkIndex.Build(S(a)); using var c=new CancellationTokenSource(); c.Cancel();
        try { WikiLinkIndex.Build(S(a),i,c.Token); throw new Exception("Missing cancellation"); } catch(OperationCanceledException) { }
        try { WikiLinks.Parse(new string('x',100000),c.Token); throw new Exception("Missing parse cancellation"); } catch(OperationCanceledException) { }
        Check(i.IsComplete);
    });
    Test("10000 notes 25MiB linear source counters warm and one-body edit",()=>{
        const int count=10000, width=2622; var target=N("Target");
        var notes=Enumerable.Range(0,count).Select(n=>N("Note"+n,"[[Target]]"+new string('x',width-10))).ToArray(); notes[0]=target with {Body=new string('x',width)};
        var s=S(notes); long total=notes.Sum(n=>(long)n.Body.Length); Check(total>=25L*1024*1024);
        var i=WikiLinkIndex.Build(s); Check(i.IsComplete && i.ParsedBodyCount==count && i.ParsedCharacterCount==total && i.LinkCount==count-1 && i.EstimatedOwnedBytes<=WikiLinkIndex.MaxOwnedBytes);
        var warm=WikiLinkIndex.Build(s with {Revision=2,Notes=notes.Select(n=>n with {Saved=true,Flair="warm"}).ToArray()},i);
        Check(warm.ParsedBodyCount==0 && warm.ParsedCharacterCount==0 && warm.ResolutionCount==0 && warm.GetBacklinks(target.Id).Count==9999);
        var edited=notes.ToArray(); edited[1]=edited[1] with {Body="[[Target]] edited"}; var one=WikiLinkIndex.Build(s with {Notes=edited},warm);
        Check(one.ParsedBodyCount==1 && one.ParsedCharacterCount==edited[1].Body.Length && one.ResolutionCount==1);
        Console.WriteLine($"stress sourceChars={total}; ownedEstimate={i.EstimatedOwnedBytes}; links={i.LinkCount}; coldParses={i.ParsedBodyCount}; warmParses={warm.ParsedBodyCount}; editParses={one.ParsedBodyCount}");
    });
    Console.WriteLine($"Wiki tests: {passed} passed"); return 0;
}
catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }



