using Threadbook.Core;

try
{
int passed = 0;
void Test(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception) { Console.Error.WriteLine("FAIL " + name); throw; } }
void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }

Test("catalog has entries of every kind, with unique ids", () =>
{
    Check(Catalog.Entries.Count >= 20, "seed size");
    Check(Catalog.Entries.Select(e => e.Id).Distinct().Count() == Catalog.Entries.Count, "unique ids");
    foreach (var kind in Enum.GetValues<CatalogKind>()) Check(Catalog.Entries.Any(e => e.Kind == kind), "kind " + kind);
});

Test("every entry is complete and safe to show", () =>
{
    foreach (var e in Catalog.Entries)
    {
        Check(e.Name.Length > 0 && e.Summary.Length > 0 && e.SourceName.Length > 0, e.Id);
        Check(Uri.TryCreate(e.SourceUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http", e.Id);
        Check(e.License.Length > 0 && e.Clients.Length > 0 && e.VerifiedOn.Length > 0, e.Id);
    }
});

Test("starter picks exist and every MCP server names its risks honestly", () =>
{
    Check(Catalog.Entries.Any(e => e.StarterPick));
    Check(Catalog.Entries.Where(e => e.Kind == CatalogKind.McpServer).All(e => e.Risks.Count > 0 || e.Id is "mcp-sequential-thinking" or "mcp-time"));
});

Test("search filters by text and kind, case-insensitively", () =>
{
    Check(Catalog.Search("playwright", null).Any(e => e.Id == "mcp-playwright"));
    Check(Catalog.Search("PLAYWRIGHT", null).Any(e => e.Id == "mcp-playwright"));
    Check(Catalog.Search("", CatalogKind.Skill).All(e => e.Kind == CatalogKind.Skill));
    Check(Catalog.Search(null, null).Count == Catalog.Entries.Count);
    Check(Catalog.Search("no-such-thing-zzz", null).Count == 0);
});

Test("search caps an over-long query instead of throwing", () =>
{
    var hits = Catalog.Search(new string('x', Catalog.MaxQueryLength + 500), null);
    Check(hits.Count == 0);
});

Test("fork text carries the facts, install and config, and a notes section", () =>
{
    var entry = Catalog.Entries.Single(e => e.Id == "mcp-filesystem");
    var text = Catalog.ForkText(entry);
    Check(text.Contains(entry.Name) && text.Contains(entry.SourceUrl) && text.Contains("Touches files"));
    Check(text.Contains("## Install") && text.Contains(entry.InstallCommand!));
    Check(text.Contains("## Config") && text.Contains(entry.ConfigSnippet!));
    Check(text.Contains("## My notes"));
    var guide = Catalog.Entries.Single(e => e.Id == "guide-anthropic-prompting");
    Check(!Catalog.ForkText(guide).Contains("## Install"), "guides have no install section");
});

Test("risk and kind labels are plain words", () =>
{
    Check(Catalog.RiskLabel(CatalogRisk.ApiKey) == "Needs an API key or token");
    Check(Catalog.KindLabel(CatalogKind.McpServer) == "MCP server");
});

Console.WriteLine($"{passed} catalog tests passed.");
return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
