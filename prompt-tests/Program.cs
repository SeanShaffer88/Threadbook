using Threadbook.Core;

try
{
int passed = 0;
void Test(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception) { Console.Error.WriteLine("FAIL " + name); throw; } }
void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
void Error(Action action, ErrorCode? code = null) { try { action(); } catch (DataException ex) { Check(code is null || ex.Code == code, ex.Code.ToString()); return; } throw new Exception("Expected DataException"); }

Test("parse finds variables in first-appearance order, deduped case-insensitively", () => {
    var vars = PromptTemplates.Parse("Write about {{topic}} for {{Audience}}. Again {{topic}} and {{audience = readers}}.");
    Check(vars.Count == 2); Check(vars[0].Name == "topic" && vars[0].Occurrences == 2 && vars[0].DefaultValue is null);
    Check(vars[1].Name == "Audience" && vars[1].Occurrences == 2 && vars[1].DefaultValue == "readers");
});
Test("parse trims names and reads defaults", () => {
    var vars = PromptTemplates.Parse("{{ tone = plain and direct }}");
    Check(vars.Count == 1 && vars[0].Name == "tone" && vars[0].DefaultValue == "plain and direct");
});
Test("parse ignores malformed, unclosed and escaped placeholders", () => {
    Check(PromptTemplates.Parse("{{}} {{ }} {{unclosed").Count == 0);
    Check(PromptTemplates.Parse("literal \\{{topic}}").Count == 0);
    Check(PromptTemplates.Parse("{{a{b}}} {{ok}}").Select(v => v.Name).SequenceEqual(new[] { "ok" }));
    Check(PromptTemplates.Parse("plain text, no blanks").Count == 0);
});
Test("parse rejects over-long names but accepts the boundary", () => {
    Check(PromptTemplates.Parse("{{" + new string('n', 61) + "}}").Count == 0);
    Check(PromptTemplates.Parse("{{" + new string('n', 60) + "}}").Count == 1);
});
Test("render substitutes every occurrence, case-insensitively", () => {
    var render = PromptTemplates.Render("Hi {{name}}, bye {{NAME}}.", new Dictionary<string, string> { ["Name"] = "Sam" });
    Check(render.Complete && render.Text == "Hi Sam, bye Sam.");
});
Test("render falls back to defaults and reports missing in order", () => {
    var render = PromptTemplates.Render("{{a}} {{b = two}} {{c}} {{a}}", new Dictionary<string, string>());
    Check(render.Text == "{{a}} two {{c}} {{a}}");
    Check(!render.Complete && render.Missing.SequenceEqual(new[] { "a", "c" }));
});
Test("render treats an empty supplied value as missing, default still applies", () => {
    var render = PromptTemplates.Render("{{a}} {{b = bee}}", new Dictionary<string, string> { ["a"] = "", ["b"] = "" });
    Check(render.Text == "{{a}} bee" && render.Missing.SequenceEqual(new[] { "a" }));
});
Test("render unescapes literal braces without substituting them", () => {
    var render = PromptTemplates.Render("Show \\{{topic}} to {{who}}.", new Dictionary<string, string> { ["who"] = "me", ["topic"] = "X" });
    Check(render.Complete && render.Text == "Show {{topic}} to me.");
});
Test("render enforces the output bound", () =>
    Error(() => PromptTemplates.Render("{{a}}", new Dictionary<string, string> { ["a"] = new string('x', 100001) }), ErrorCode.CapacityExceeded));
Test("parse enforces the source bound", () =>
    Error(() => PromptTemplates.Parse(new string('x', 100001)), ErrorCode.CapacityExceeded));
Test("slash detection at word starts only", () => {
    Check(PromptTemplates.DetectSlash("/ro", 3) == new SlashQuery(0, 3, "ro"));
    Check(PromptTemplates.DetectSlash("start /con", 10) == new SlashQuery(6, 4, "con"));
    Check(PromptTemplates.DetectSlash("a/b", 3) is null);
    Check(PromptTemplates.DetectSlash("/two words", 10) is null);
    Check(PromptTemplates.DetectSlash("no slash", 8) is null);
    Check(PromptTemplates.DetectSlash("/", 1) == new SlashQuery(0, 1, ""));
    Check(PromptTemplates.DetectSlash("line\n/for", 9) == new SlashQuery(5, 4, "for"));
});
Test("slash detection rejects caret out of range", () => {
    try { PromptTemplates.DetectSlash("x", 2); } catch (ArgumentOutOfRangeException) { return; }
    throw new Exception("Range accepted");
});
Test("block catalog is complete and searchable", () => {
    Check(PromptTemplates.Blocks.Count == 11);
    Check(PromptTemplates.Blocks.Select(b => b.Command).Distinct().Count() == 11);
    Check(PromptTemplates.FindBlocks("").Count == 11);
    Check(PromptTemplates.FindBlocks("form").Select(b => b.Command).SequenceEqual(new[] { "format" }));
    Check(PromptTemplates.FindBlocks("fill-in").Select(b => b.Command).SequenceEqual(new[] { "variable" }));
    Check(PromptTemplates.FindBlocks("zzz").Count == 0);
    foreach (var block in PromptTemplates.Blocks) Check(block.Snippet.Length > 0 && block.Title.Length > 0, block.Command);
});
Test("apply block replaces the slash query and lands the caret after the snippet", () => {
    var block = PromptTemplates.Blocks.Single(b => b.Command == "role");
    var edit = PromptTemplates.ApplyBlock("Write /ro now", new SlashQuery(6, 3, "ro"), block);
    Check(edit.Text == "Write " + block.Snippet + " now");
    Check(edit.SelectionStart == 6 + block.Snippet.Length && edit.SelectionLength == 0 && edit.ReplacementCount == 1);
});
Test("apply block enforces the body bound", () => {
    var block = PromptTemplates.Blocks.Single(b => b.Command == "role");
    Error(() => PromptTemplates.ApplyBlock(new string('x', 100000), new SlashQuery(0, 1, ""), block), ErrorCode.CapacityExceeded);
});
Test("end to end: insert a block, fill its variables, render complete", () => {
    var block = PromptTemplates.Blocks.Single(b => b.Command == "format");
    var edit = PromptTemplates.ApplyBlock("/", new SlashQuery(0, 1, ""), block);
    var vars = PromptTemplates.Parse(edit.Text);
    Check(vars.Count == 1 && vars[0].Name == "format" && vars[0].DefaultValue == "a clear Markdown answer");
    var render = PromptTemplates.Render(edit.Text, new Dictionary<string, string>());
    Check(render.Complete && render.Text == "Output format:\na clear Markdown answer\n");
});
Console.WriteLine($"{passed} prompt tests passed");
}
catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
