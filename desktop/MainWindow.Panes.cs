using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Threadbook.Core;

namespace Threadbook.Desktop;

public sealed partial class MainWindow
{
    private void RenderNotebookTree()
    {
        var counts = app.State.Notes.GroupBy(n => n.NotebookId).ToDictionary(g => g.Key, g => g.Count());
        var books = app.State.Notebooks.OrderBy(b => b.Created).ToArray();
        var assigned = library is null ? new Dictionary<Guid, Guid>() : library.State.NotebookFolders;
        var folderBooks = books.Where(b => assigned.ContainsKey(b.Id)).GroupBy(b => assigned[b.Id]).ToDictionary(g => g.Key, g => g.ToArray());
        void Book(Notebook book, int depth) {
            var row = new DockPanel { Margin = new Thickness(depth * 12, 1, 0, 1) };
            var more = Btn("···", () => ManageNotebook(book)); more.Padding = new Thickness(3); more.BorderThickness = new Thickness(0); more.ToolTip = "Manage " + book.Name; AutomationProperties.SetName(more, "Manage notebook " + book.Name); DockPanel.SetDock(more, Dock.Right); row.Children.Add(more);
            var count = Label(counts.GetValueOrDefault(book.Id).ToString(), 12, false, "Muted"); count.VerticalAlignment = VerticalAlignment.Center; count.Margin = new Thickness(0, 0, 6, 0); DockPanel.SetDock(count, Dock.Right); row.Children.Add(count);
            var button = Nav("n/" + book.Name, () => Navigate(book.Id, false)); button.Margin = new Thickness(0); button.Padding = new Thickness(8, 5, 8, 5); button.Content = new TextBlock { Text = "n/" + book.Name, TextTrimming = TextTrimming.CharacterEllipsis }; button.ToolTip = book.Name;
            if (book.Id == notebookId && !savedOnly) { button.SetResourceReference(Control.BackgroundProperty, "Selected"); button.BorderThickness = new Thickness(4, 0, 0, 0); button.SetResourceReference(Control.BorderBrushProperty, "Accent"); }
            row.Children.Add(button); notebooks.Children.Add(row);
        }
        foreach (var book in books.Where(b => !assigned.ContainsKey(b.Id))) Book(book, 0);
        if (library is null) return;
        var children = library.State.Folders.Where(f => f.ParentId is not null).GroupBy(f => f.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToArray());
        var visited = new HashSet<Guid>();
        void Folder(LibraryFolder folder, int depth) {
            if (depth >= 8 || !visited.Add(folder.Id)) return;
            var label = Label(folder.Name, 13, true); label.Margin = new Thickness(depth * 12, 14, 0, 4); notebooks.Children.Add(label);
            if (folderBooks.TryGetValue(folder.Id, out var contained)) foreach (var book in contained) Book(book, depth + 1);
            if (children.TryGetValue(folder.Id, out var nested)) foreach (var child in nested) Folder(child, depth + 1);
        }
        foreach (var folder in library.State.Folders.Where(f => f.ParentId is null)) Folder(folder, 0);
        notebooks.Children.Add(Rule()); notebooks.Children.Add(Label("TAGS", 12, true, "Muted"));
        var tagCounts = new Dictionary<Guid, int>(); foreach (var pair in library.State.NoteTags) foreach (var tag in pair.Value) tagCounts[tag] = tagCounts.GetValueOrDefault(tag) + 1;
        var tags = new StackPanel();
        foreach (var tag in library.State.Tags.OrderBy(t => t.Name).Take(200)) {
            var button = Nav(tag.Name, () => { if (!MayLeave()) return; libraryFilters = libraryFilters with { TagIds = new[] { tag.Id } }; RefreshFeed(); });
            var tagRow = new DockPanel(); var tagCount = Label(tagCounts.GetValueOrDefault(tag.Id).ToString(), 12, false, "Muted"); tagCount.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(tagCount, Dock.Right); tagRow.Children.Add(tagCount); tagRow.Children.Add(Label(tag.Name, 13)); button.Content = tagRow; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(8, 5, 8, 5); button.Margin = new Thickness(0, 1, 0, 1); tags.Children.Add(button);
        }
        notebooks.Children.Add(tags);
        if (library.State.Tags.Count > 200) notebooks.Children.Add(Btn("All tags / filters…", OpenLibraryFilters));
    }
}
