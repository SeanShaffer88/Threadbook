using Threadbook.Core;
using Threadbook.Storage;
using System.Text.Json;

try
{
int passed = 0;
void Test(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception) { Console.Error.WriteLine("FAIL " + name); throw; } }
void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
void Error(Action action, ErrorCode? code = null) { try { action(); } catch (DataException ex) { Check(code is null || ex.Code == code, ex.Code.ToString()); return; } throw new Exception("Expected DataException"); }
Test("duplicate headings CRLF offsets exclude fence", () => {
    string source = "# repeat\r\n```\r\n# hidden\r\n```\r\n# repeat";
    var doc = Markdown.Parse(source); Check(doc.Headings.Count == 2); Check(doc.Headings[1].SourceStart == 30); Check(doc.Headings.All(h => source.Substring(h.SourceStart, h.SourceLength) == "# repeat"));
});
Test("all heading levels and CR/LF offsets", () => {
    var d = Markdown.Parse("# a\r## b\n### c\r\n#### d\n##### e\n###### f\n####### literal");
    Check(d.Headings.Select(h => h.Level).SequenceEqual(new[]{1,2,3,4,5,6})); Check(d.Blocks[^1].Kind == MarkdownBlockKind.Paragraph);
});
Test("fence marker length and inert content", () => { var d = Markdown.Parse("~~~~ lang\n**raw**\n~~~\n# hidden\n~~~~\n# visible"); Check(d.Headings.Count == 1); Check(d.Blocks[0].Text.Contains("# hidden")); });
Test("unclosed fence preserved and no false outline", () => { string s="```\n# hidden"; var d=Markdown.Parse(s); Check(d.Diagnostics.Count==1 && d.Headings.Count==0 && d.Blocks[0].Text==s); });
Test("inline required subset source spans", () => {
    string s="**bold** *italic* ~~strike~~ `code` [link](javascript:x) ![alt](file:x)";
    var spans=Markdown.ParseInline(s, 7); Check(spans.Where(x=>x.Kind!=MarkdownInlineKind.Text).Select(x=>x.Kind).SequenceEqual(new[]{MarkdownInlineKind.Bold,MarkdownInlineKind.Italic,MarkdownInlineKind.Strike,MarkdownInlineKind.Code,MarkdownInlineKind.Link,MarkdownInlineKind.Image}));
    Check(spans.Sum(x=>x.SourceLength)==s.Length); Check(spans[0].SourceStart==7); Check(spans[^1].Destination=="file:x");
});
Test("wiki composites preserve source and ordinary flat styling", () => {
    string raw="**[[A]]**";
    var span=Markdown.ParseInline(raw).Single(); Check(span.Children!.Single().Wiki!.SourceStart==2);
    Check(Markdown.ParseInline("**plain *nested* text**").Single().Children is null);
    Check(Markdown.ExportHtml("local",raw).Contains("<strong>[[A]]</strong>"));
});
Test("malformed syntax literal", () => { foreach(var s in new[]{"**unclosed","[broken](", "![bad]", "<script>alert(1)</script>","[reference][id]", "    - nested"}) Check(string.Concat(Markdown.Parse(s).Blocks.SelectMany(b=>b.Inlines).Select(x=>x.Text))==s,s); });
Test("flat lists tasks quote rule", () => { var d=Markdown.Parse("- one\n2. two\n* [x] done\n- [ ] todo\n> quote\n---"); Check(d.Blocks.Select(b=>b.Kind).SequenceEqual(new[]{MarkdownBlockKind.UnorderedItem,MarkdownBlockKind.OrderedItem,MarkdownBlockKind.TaskItem,MarkdownBlockKind.TaskItem,MarkdownBlockKind.Quote,MarkdownBlockKind.Rule})); Check(d.Blocks[2].Checked&&!d.Blocks[3].Checked); });
Test("rectangular pipe table inline and offsets", () => { string s="| **A** | B |\n| :--- | ---: |\n| c | *d* |"; var d=Markdown.Parse(s); var t=d.Blocks.Single(); Check(t.Kind==MarkdownBlockKind.Table&&t.Columns==2&&t.Cells!.Count==4); Check(t.Cells![0][0].Kind==MarkdownInlineKind.Bold); Check(s.Substring(t.Cells[3][0].SourceStart,t.Cells[3][0].SourceLength)=="*d*"); });
Test("malformed table remains visible", () => { var d=Markdown.Parse("| a | b |\n| --- | --- |\n| c |"); Check(d.Blocks.All(b=>b.Kind!=MarkdownBlockKind.Table)); });
Test("malformed 90K table bounded and literal", () => {
    string s=string.Concat(Enumerable.Repeat("|---|---|\n",9000))+"|x|";
    var watch=System.Diagnostics.Stopwatch.StartNew(); var d=Markdown.Parse(s); watch.Stop();
    Console.WriteLine($"malformed-table90k: {watch.Elapsed.TotalMilliseconds:F1} ms");
    Check(watch.Elapsed < TimeSpan.FromSeconds(5), "Malformed table exceeded 5 seconds");
    Check(d.Blocks.Count==9001 && d.Blocks.All(b=>b.Kind==MarkdownBlockKind.Paragraph));
    foreach(var b in d.Blocks) Check(s.Substring(b.SourceStart,b.SourceLength)==b.Text && string.Concat(b.Inlines.Select(x=>x.Text))==b.Text);
});
Test("escaped pipes reject entire candidate literally", () => {
    foreach(string s in new[]{"| a\\|b | c |\n| --- | --- | --- |", "| a | b |\n| --- | --- |\n| c\\|d | e |", "| a | b |\n| --- | --- |\n| c | d |\n| e\\|f | g |"}) {
        var d=Markdown.Parse(s); Check(d.Blocks.All(b=>b.Kind==MarkdownBlockKind.Paragraph));
        Check(string.Join("\n",d.Blocks.Select(b=>string.Concat(b.Inlines.Select(x=>x.Text))))==s);
    }
});
Test("table after failed width retains CRLF source offsets", () => {
    string s="|a|b|\r\n|---|---|\r\n|x|y|z|\r\n|---|---|---|\r\n|1|2|3|";
    var d=Markdown.Parse(s); var t=d.Blocks.Last();
    Check(d.Blocks.Count==3 && t.Kind==MarkdownBlockKind.Table && t.Columns==3);
    Check(t.SourceStart==s.IndexOf("|x|",StringComparison.Ordinal) && s.Substring(t.SourceStart,t.SourceLength)==t.Text);
    foreach(var cell in t.Cells!) foreach(var span in cell) Check(s.Substring(span.SourceStart,span.SourceLength)==span.Text);
});
Test("safe self contained HTML adversarial values", () => {
    string s="<script>alert(1)</script>\n[evil](javascript:alert(1))\n![x](https://example.invalid/x)\n[data](data:text/html,<svg onload=x>)\n[file](file:///private)\n`<img onerror=x>`";
    string html=Markdown.ExportHtml("</title><script>x</script>",s);
    Check(!html.Contains("<script",StringComparison.OrdinalIgnoreCase)&&!html.Contains("<img",StringComparison.OrdinalIgnoreCase));
    Check(!html.Contains("href=",StringComparison.OrdinalIgnoreCase)&&!html.Contains("src=",StringComparison.OrdinalIgnoreCase)); Check(html.Contains("&lt;script&gt;")&&html.Contains("<style>")&&html.Contains("default-src 'none'"));
});
Test("source word count unicode whitespace", () => { Check(Markdown.CountSourceWords("\t猫\u2003😀\r\n**hi** e\u0301")==4); Check(Markdown.CountSourceWords("")==0); });
Test("bounded parse", () => Error(()=>Markdown.Parse(new string('x',100001)),ErrorCode.CapacityExceeded));
Test("adversarial 100K unmatched delimiters terminates", () => { foreach(char c in new[]{'[','*','_','`'}) { var d=Markdown.Parse(new string(c,100000)); Check(d.Blocks.Count>0); } });
Test("find ordinal unicode and literal regex metacharacters", () => { Check(TextEditing.Find("猫😀猫😀","😀").Select(m=>m.Start).SequenceEqual(new[]{1,4})); Check(TextEditing.Find("a.b A.B axb","a.b").Count==2); Check(TextEditing.Find("Aa","a",true).Count==1); });
Test("find empty and non overlapping", () => { Check(TextEditing.Find("aaa","aa").Count==1); Check(TextEditing.Find("abc","").Count==0); });
Test("navigation wraps and previous", () => { Check(TextEditing.Navigate("a a","a",3)!.Start==0); Check(TextEditing.Navigate("a a","a",-1,true)!.Start==2); Check(TextEditing.Navigate("a a","a",1,true)!.Start==0); });
Test("replace current requires matching selection", () => { Check(TextEditing.ReplaceCurrent("abc abc","abc","X",1,3).ReplacementCount==0); var e=TextEditing.ReplaceCurrent("ABC abc","abc","猫",0,3); Check(e.Text=="猫 abc"&&e.SelectionStart==0&&e.SelectionLength==1); });
Test("replace all original buffer only", () => { var e=TextEditing.ReplaceAll("aaa aaa","aa","aaaa"); Check(e.Text=="aaaaa aaaaa"&&e.ReplacementCount==2); Check(TextEditing.ReplaceAll("abc","","x").Text=="abc"); });
Test("replace result bounded", () => Error(()=>TextEditing.ReplaceAll(new string('a',100000),"a","aa"),ErrorCode.CapacityExceeded));
Test("replace invalid range rejected", () => { try { TextEditing.ReplaceCurrent("a","a","x",int.MaxValue,1); } catch(ArgumentOutOfRangeException) { return; } throw new Exception("Range accepted"); });
Test("replace current output limit", () => Error(()=>TextEditing.ReplaceCurrent(new string('a',100000),"a","aa",0,1),ErrorCode.CapacityExceeded));
Test("ordered source numbering retained in HTML", () => { var d=Markdown.Parse("42. answer"); Check(d.Blocks[0].Level==42); Check(Markdown.ExportHtml("title","42. answer").Contains("<ol start=\"42\">")); });
Test("empty document and escaping", () => { Check(Markdown.Parse("").Blocks.Count==0); Check(Markdown.ExportHtml("a&b","\"<x>&").Contains("&quot;&lt;x&gt;&amp;")); });
string root=Path.Combine(Path.GetTempPath(),"Threadbook-writing-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {
    using var owner=new JsonStore(root); var snapshot=owner.Load(); var store=new RecoveryStore(owner);
    var draft=new RecoveryDraft(1,Guid.NewGuid(),snapshot.Notebooks[0].Id,null,0,"","猫😀 **draft**","",DateTimeOffset.UtcNow);
    Test("missing sidecars no initialization",()=>Check(store.LoadDraft() is null&&store.LoadSession() is null));
    Test("blank new note draft roundtrip",()=>{Check(store.SaveDraft(draft).Success);Check(store.LoadDraft()==draft);});
    Test("replacement leaves canonical bytes unchanged",()=>{var before=File.ReadAllBytes(owner.PrimaryPath);Check(store.SaveDraft(draft with{Body="changed"}).Success);Check(before.SequenceEqual(File.ReadAllBytes(owner.PrimaryPath)));Check(!Directory.GetFiles(root,".recovery-pending-*").Any());});
    Test("session roundtrip bounded caret",()=>{var s=new RecoverySession(1,draft.NotebookId,null,3,DateTimeOffset.UtcNow);Check(store.SaveSession(s).Success&&store.LoadSession()==s);Check(!store.SaveSession(s with{Caret=100001}).Success);});
    Test("invalid fields do not replace good draft",()=>{var before=File.ReadAllBytes(store.DraftPath);Check(!store.SaveDraft(draft with{Title=new string('x',201)}).Success);Check(!store.SaveDraft(draft with{NoteId=Guid.Empty}).Success);Check(!store.SaveDraft(draft with{BaseRevision=-1}).Success);Check(before.SequenceEqual(File.ReadAllBytes(store.DraftPath)));});
    Test("corrupt draft preserved on load and save",()=>{File.WriteAllText(store.DraftPath,"{truncated");var before=File.ReadAllBytes(store.DraftPath);Error(()=>store.LoadDraft(),ErrorCode.CorruptData);Check(!store.SaveDraft(draft).Success);Check(before.SequenceEqual(File.ReadAllBytes(store.DraftPath)));});
    Test("explicit discard permits new draft",()=>{Check(store.DiscardDraft().Success);Check(store.SaveDraft(draft).Success);});
    Test("unsupported schema preserved",()=>{File.WriteAllText(store.DraftPath,JsonSerializer.Serialize(draft with{SchemaVersion=2}));Error(()=>store.LoadDraft(),ErrorCode.UnsupportedVersion);Check(store.SaveDraft(draft).Error?.Code==ErrorCode.UnsupportedVersion);Check(store.DiscardDraft().Success);});
    Test("duplicate properties preserved",()=>{File.WriteAllText(store.DraftPath,"{\"SchemaVersion\":1,\"SchemaVersion\":1}");Error(()=>store.LoadDraft(),ErrorCode.CorruptData);Check(!store.SaveDraft(draft).Success);Check(store.DiscardDraft().Success);});
    Test("oversized sidecar rejected preserved",()=>{File.WriteAllText(store.DraftPath,new string('x',RecoveryStore.MaxDraftBytes+1));Error(()=>store.LoadDraft(),ErrorCode.CapacityExceeded);Check(!store.SaveDraft(draft).Success);Check(new FileInfo(store.DraftPath).Length==RecoveryStore.MaxDraftBytes+1);Check(store.DiscardDraft().Success);});
    Test("corrupt session independent of valid draft",()=>{Check(store.SaveDraft(draft).Success);File.WriteAllText(store.SessionPath,"null");Error(()=>store.LoadSession(),ErrorCode.CorruptData);Check(store.LoadDraft()==draft);Check(store.DiscardSession().Success);});
    Test("write failure preserves sidecar",()=>{var before=File.ReadAllBytes(store.DraftPath);using(var held=new FileStream(store.DraftPath,FileMode.Open,FileAccess.Read,FileShare.None)) Check(!store.SaveDraft(draft with{Body="fail"}).Success);Check(before.SequenceEqual(File.ReadAllBytes(store.DraftPath)));});
    Test("disposed owner cannot write unlocked",()=>{owner.Dispose();Check(store.SaveDraft(draft).Error?.Code==ErrorCode.Conflict);});
} finally { Directory.Delete(root,true); }
Console.WriteLine($"{passed} writing backend tests passed");
}
catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
