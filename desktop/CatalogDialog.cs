using System.Windows;
using System.Windows.Controls;
using Threadbook.Core;

namespace Threadbook.Desktop;

/// <summary>The built-in catalog of MCP servers, skills, prompt packs and guides. Browsing and copying only; adding an entry saves it as a note through the owner's command path.</summary>
internal sealed class CatalogDialog : EditorWindow
{
    private readonly Func<string, string, bool> addToNotebook;
    private readonly TextBox search = new() { MaxLength = Catalog.MaxQueryLength };
    private readonly ComboBox kinds = new();
    private readonly ListBox results = new() { Height = 220 };
    private readonly TextBlock detail = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 4) };
    private readonly Button copyInstall = new() { Content = "Copy install command" };
    private readonly Button copyConfig = new() { Content = "Copy config" };
    private readonly Button copyLink = new() { Content = "Copy source link" };
    private readonly Button add = new() { Content = "Add to my prompts" };

    /// <summary>True when at least one entry was saved as a note, so the owner can refresh.</summary>
    public bool AddedAny { get; private set; }

    public CatalogDialog(Window owner, Func<string, string, bool> addToNotebook) : base(owner, "Catalog — MCP servers, skills and prompts")
    {
        this.addToNotebook = addToNotebook;
        Width = 760; Height = 820;
        Fields.Children.Add(new TextBlock { Text = "A bundled starter catalog. Nothing here is downloaded or installed by Threadbook — copy what you want, check it at the source, and add entries to a notebook to keep your own notes on them.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });

        var searchCaption = new Label { Content = "Search the catalog", Target = search, Padding = new Thickness(0, 10, 0, 5) };
        searchCaption.SetResourceReference(Control.ForegroundProperty, "Text");
        System.Windows.Automation.AutomationProperties.SetName(search, "Search the catalog");
        Fields.Children.Add(searchCaption); Fields.Children.Add(search);
        search.TextChanged += (_, _) => RefreshList();

        var kindCaption = new Label { Content = "Kind", Target = kinds, Padding = new Thickness(0, 10, 0, 5) };
        kindCaption.SetResourceReference(Control.ForegroundProperty, "Text");
        kinds.Items.Add("Everything"); kinds.Items.Add("MCP servers"); kinds.Items.Add("Skills"); kinds.Items.Add("Prompt packs"); kinds.Items.Add("Guides");
        kinds.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetName(kinds, "Kind");
        Fields.Children.Add(kindCaption); Fields.Children.Add(kinds);
        kinds.SelectionChanged += (_, _) => RefreshList();

        var listCaption = new Label { Content = "Entries", Target = results, Padding = new Thickness(0, 10, 0, 5) };
        listCaption.SetResourceReference(Control.ForegroundProperty, "Text");
        System.Windows.Automation.AutomationProperties.SetName(results, "Entries");
        Fields.Children.Add(listCaption); Fields.Children.Add(results);
        results.SelectionChanged += (_, _) => RefreshDetail();

        Fields.Children.Add(detail);
        Fields.Children.Add(Error);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        copyInstall.Click += (_, _) => Copy(Selected()?.InstallCommand, "Install command copied.");
        copyConfig.Click += (_, _) => Copy(Selected()?.ConfigSnippet, "Config copied.");
        copyLink.Click += (_, _) => Copy(Selected()?.SourceUrl, "Source link copied.");
        add.Click += (_, _) => AddSelected();
        var close = new Button { Content = "Close", IsCancel = true };
        row.Children.Add(copyInstall); row.Children.Add(copyConfig); row.Children.Add(copyLink); row.Children.Add(add); row.Children.Add(close);
        Fields.Children.Add(row);
        RefreshList();
        Loaded += (_, _) => search.Focus();
    }

    protected override bool MayClose() => true;

    private CatalogKind? KindFilter => kinds.SelectedIndex switch
    {
        1 => CatalogKind.McpServer,
        2 => CatalogKind.Skill,
        3 => CatalogKind.PromptPack,
        4 => CatalogKind.Guide,
        _ => null,
    };

    private CatalogEntry? Selected() => results.SelectedItem is CatalogEntry entry ? entry : null;

    private void RefreshList()
    {
        var keep = Selected()?.Id;
        results.Items.Clear();
        foreach (var entry in Catalog.Search(search.Text, KindFilter)) results.Items.Add(entry);
        results.DisplayMemberPath = nameof(CatalogEntry.Name);
        var next = results.Items.Cast<CatalogEntry>().FirstOrDefault(e => e.Id == keep) ?? results.Items.Cast<CatalogEntry>().FirstOrDefault();
        results.SelectedItem = next;
        RefreshDetail();
    }

    private void RefreshDetail()
    {
        var entry = Selected();
        copyInstall.IsEnabled = entry?.InstallCommand is not null;
        copyConfig.IsEnabled = entry?.ConfigSnippet is not null;
        copyLink.IsEnabled = entry is not null;
        add.IsEnabled = entry is not null;
        if (entry is null) { detail.Text = "No entries match. Try a shorter search."; return; }
        var risks = entry.Risks.Count == 0 ? "Nothing on your machine" : string.Join(", ", entry.Risks.Select(Catalog.RiskLabel));
        detail.Text = $"{entry.Name}\n{Catalog.KindLabel(entry.Kind)} · {entry.SourceName}{(entry.StarterPick ? " · Starter pick" : "")}\n\n{entry.Summary}\n\nLicense: {entry.License}\nWorks with: {entry.Clients}\nCan touch: {risks}\nCatalog verified: {entry.VerifiedOn}\nSource: {entry.SourceUrl}";
    }

    private void Copy(string? text, string done)
    {
        if (text is null) return;
        try { Clipboard.SetText(text); Error.Text = done; }
        catch (System.Runtime.InteropServices.ExternalException) { Error.Text = "The clipboard is busy. Try again."; }
    }

    private void AddSelected()
    {
        var entry = Selected(); if (entry is null) return;
        if (addToNotebook(entry.Name, Catalog.ForkText(entry))) { AddedAny = true; Error.Text = $"“{entry.Name}” saved as a note in your notebook."; }
        else Error.Text = "Could not save the note. Create a notebook first, then try again.";
    }
}
