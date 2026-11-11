using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Threadbook.Core;
namespace Threadbook.Desktop;

public sealed partial class MainWindow
{
    private WikiLinkIndex? wikiIndex;
    private Snapshot? wikiSnapshot, wikiRequestedSnapshot;
    private CancellationTokenSource? wikiCancellation;
    private long wikiGeneration, wikiEpoch;
    private bool wikiClosed;
    // Optional host adapter; normal startup always uses the Core background builder.
    private Func<Snapshot, WikiLinkIndex?, CancellationToken, Task<WikiLinkIndex>>? wikiBuildFactory = null;
    private Task? wikiBuildTask;
    private readonly StackPanel wikiBacklinks = new();
    private RichTextBox? wikiReader;
    private Guid? wikiReaderId;
    private Window? wikiChooser;
    private TextBox? wikiChooserQuery;
    private ListBox? wikiChooserResults;
    private int wikiBacklinkLimit = 30;

    private bool WikiCurrent => InlineUiUnlocked && !wikiClosed && wikiIndex is not null && ReferenceEquals(wikiSnapshot, app.State) && wikiIndex.Revision == app.State.Revision;
    private void RequestWikiIndex()
    {
        if (!InlineUiUnlocked || wikiClosed || ReferenceEquals(wikiRequestedSnapshot, app.State)) return;
        wikiCancellation?.Cancel(); wikiCancellation?.Dispose();
        var cancellation = wikiCancellation = new CancellationTokenSource(); var token = cancellation.Token;
        var snapshot = wikiRequestedSnapshot = app.State; var previous = wikiIndex;
        long generation = ++wikiGeneration, epoch = wikiEpoch, revision = snapshot.Revision;
        wikiBuildTask = BuildWikiIndex(snapshot, previous, token, generation, epoch, revision);
    }
    private async Task BuildWikiIndex(Snapshot snapshot, WikiLinkIndex? previous, CancellationToken token, long generation, long epoch, long revision)
    {
        try {
            var result = await (wikiBuildFactory?.Invoke(snapshot, previous, token)
                ?? Task.Run(() => WikiLinkIndex.Build(snapshot, previous, token), token)).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() => {
            if (token.IsCancellationRequested || !InlineUiUnlocked || wikiClosed || generation != wikiGeneration || epoch != wikiEpoch || revision != app.State.Revision || !ReferenceEquals(snapshot, app.State)) return;
            wikiIndex = result; wikiSnapshot = snapshot;
            RefreshWikiBacklinks();
            if (wikiReader is not null && app.State.Notes.FirstOrDefault(n => n.Id == wikiReaderId) is { } note)
                wikiReader.Document = WikiDocument(note);
            });
        } catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is DataException or ArgumentException or InvalidOperationException) {
            await Dispatcher.InvokeAsync(() => { if (!token.IsCancellationRequested && InlineUiUnlocked && !wikiClosed && generation == wikiGeneration && epoch == wikiEpoch) {
                wikiRequestedSnapshot = null; wikiBacklinks.Children.Clear(); wikiBacklinks.Children.Add(Label("Local link index unavailable. Refresh the note to retry. " + ex.Message, 12));
            } });
        }
    }
    private string? WikiState(Guid notebook, WikiLinkToken token)
    {
        if (!WikiCurrent) return "index pending";
        var resolution = wikiIndex!.Resolve(notebook, token);
        return resolution.Kind switch { WikiResolutionKind.Broken => "missing note", WikiResolutionKind.Ambiguous => "choose note", _ => null };
    }
    private System.Windows.Documents.FlowDocument WikiDocument(Note note)
    {
        var expected = app.State;
        return MarkdownView.Document(note.Body, onWikiLink: token => ActivateWikiLink(note.NotebookId, token, expected), wikiState: token => WikiState(note.NotebookId, token));
    }
    private RichTextBox WikiViewer(Note note)
    {
        wikiReaderId = note.Id;
        wikiReader = MarkdownView.Viewer(""); wikiReader.Document = WikiDocument(note); return wikiReader;
    }
    private void RefreshWikiBacklinks()
    {
        wikiBacklinks.Children.Clear();
        if (!InlineUiUnlocked || selectedId is not Guid target || !app.State.Notes.Any(n => n.Id == target)) return;
        if (!WikiCurrent) { wikiBacklinks.Children.Add(Label("Backlinks · index pending", 12)); return; }
        var index = wikiIndex!; var expected = app.State;
        var ids = index.GetBacklinks(target).Distinct().ToArray();
        wikiBacklinks.Children.Add(Label($"Backlinks · {ids.Length} notes", 15, true));
        if (!index.IsComplete) wikiBacklinks.Children.Add(Label("Incomplete index: some links and backlinks may be missing.", 12));
        foreach (var source in ids.Take(wikiBacklinkLimit)) {
            var note = app.State.Notes.FirstOrDefault(n => n.Id == source); if (note is null) continue;
            string book = app.State.Notebooks.FirstOrDefault(b => b.Id == note.NotebookId)?.Name ?? "Missing notebook";
            wikiBacklinks.Children.Add(Nav(note.Title + " — n/" + book, () => NavigateWiki(source, () => WikiCurrent && ReferenceEquals(expected, app.State) && ReferenceEquals(index, wikiIndex) && index.GetBacklinks(target).Contains(source))));
        }
        if (ids.Length > wikiBacklinkLimit) wikiBacklinks.Children.Add(Btn("Load more backlinks", () => { if (!WikiCurrent || !ReferenceEquals(index, wikiIndex)) return; wikiBacklinkLimit += 30; RefreshWikiBacklinks(); }));
    }
    private void ActivateWikiLink(Guid notebook, WikiLinkToken token, Snapshot expected)
    {
        if (!InlineUiUnlocked || wikiClosed) return;
        if (!WikiCurrent || !ReferenceEquals(expected, app.State)) { RequestWikiIndex(); SetStatus("Local links changed. Refresh the preview and try again."); return; }
        var index = wikiIndex!; var resolution = index.Resolve(notebook, token);
        bool Valid(Guid id) {
            if (!WikiCurrent || !ReferenceEquals(expected, app.State) || !ReferenceEquals(index, wikiIndex)) return false;
            var current = index.Resolve(notebook, token);
            return current.Kind == WikiResolutionKind.Resolved && current.TargetId == id;
        }
        if (resolution.Kind == WikiResolutionKind.Resolved && resolution.TargetId is Guid id) { NavigateWiki(id, () => Valid(id)); return; }
        if (resolution.Kind == WikiResolutionKind.Broken) { SetStatus("This local note link has no current target. No note was created."); return; }
        if (resolution.CandidateGroupId is not int group) return;
        var currentNotes = app.State.Notes.ToDictionary(n => n.Id);
        OpenWikiChooser(this, "Choose linked note", (offset, count, query) => {
            if (!WikiCurrent || !ReferenceEquals(index, wikiIndex) || !ReferenceEquals(expected, app.State)) return Array.Empty<Note>();
            // Shared Core groups are paged, never copied for every token.
            return index.GetCandidates(group, 0, 10000).Select(id => currentNotes.GetValueOrDefault(id)).OfType<Note>().Where(n => WikiMatches(n, query)).Skip(offset).Take(count).ToArray();
        }, id => NavigateWiki(id, () => WikiCurrent && ReferenceEquals(expected, app.State) && ReferenceEquals(index, wikiIndex) && index.Resolve(notebook, token).CandidateGroupId == group && index.GetCandidates(group, 0, 10000).Contains(id)));
    }
    private bool NavigateWiki(Guid id, Func<bool> valid)
    {
        if (!InlineUiUnlocked || wikiClosed) return false;
        if (!valid() || !app.State.Notes.Any(n => n.Id == id)) { RequestWikiIndex(); SetStatus("Local link changed. Refresh and try again."); return false; }
        if (!TryLeave(LeaveReason.Navigation)) return false;
        if (!InlineUiUnlocked || !valid() || !app.State.Notes.Any(n => n.Id == id)) { if (InlineUiUnlocked) { RequestWikiIndex(); SetStatus("Local link changed while leaving. Try again."); } return false; }
        return TryNavigate(null, id, reveal: true);
    }
    private bool WikiMatches(Note note, string query) => note.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || (app.State.Notebooks.FirstOrDefault(b => b.Id == note.NotebookId)?.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    internal void AttachWikiSurface(NoteEditingSurface surface, Func<bool> canAct)
    {
        surface.WikiPreviewContext = () => {
            var previewSnapshot = app.State;
            var notebook = surface.Fields.NotebookId;
            return (token => { if (canAct() && InlineUiUnlocked) ActivateWikiLink(notebook, token, previewSnapshot); },
                token => ReferenceEquals(previewSnapshot, app.State) ? WikiState(notebook, token) : "index pending");
        };
        surface.InsertNoteLinkRequested += (_, _) => {
            if (!canAct() || !InlineUiUnlocked) return;
            int start = surface.body.SelectionStart, length = surface.body.SelectionLength; string original = surface.body.Text;
            var expected = app.State;
            OpenWikiChooser(Window.GetWindow(surface) ?? this, "Insert note link", (offset, count, query) => {
                if (!canAct() || !InlineUiUnlocked) return Array.Empty<Note>();
                return app.State.Notes.Where(n => WikiMatches(n, query)).OrderBy(n => n.Title, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Id).Skip(offset).Take(count).ToArray();
            }, id => {
                if (!canAct() || !InlineUiUnlocked || !ReferenceEquals(expected, app.State) || surface.body.Text != original) return false;
                var note = app.State.Notes.FirstOrDefault(n => n.Id == id); if (note is null) return false;
                return surface.InsertWikiLink(id, note.Title, start, length);
            });
        };
    }
    private void OpenWikiChooser(Window owner, string title, Func<int,int,string,IReadOnlyList<Note>> page, Func<Guid,bool> activate)
    {
        if (!InlineUiUnlocked || wikiClosed) return;
        CloseWikiChooser();
        var window = wikiChooser = new Window { Owner = owner, Title = title, Width = 620, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        ApplicationBrand.Apply(window); window.SetResourceReference(BackgroundProperty, "Bg"); window.SetResourceReference(ForegroundProperty, "Text");
        var panel = new DockPanel { Margin = new Thickness(18) };
        var outline = new Border { BorderThickness = new Thickness(1), Child = panel };
        outline.SetResourceReference(Border.BorderBrushProperty, "Line");
        window.Content = outline;
        var query = wikiChooserQuery = new TextBox { MaxLength = 200, MinHeight = 36 };
        AutomationProperties.SetName(query, "Search live note titles and notebooks"); DockPanel.SetDock(query, Dock.Top); panel.Children.Add(query);
        var controls = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(controls, Dock.Bottom); panel.Children.Add(controls);
        var message = Label("Choose a note explicitly. Cancel leaves your draft unchanged.",12); DockPanel.SetDock(message, Dock.Bottom); panel.Children.Add(message);
        var list = wikiChooserResults = new ListBox { DisplayMemberPath = "Label" }; AutomationProperties.SetName(list, title + " results"); panel.Children.Add(list);
        list.SetResourceReference(Control.BackgroundProperty, "Surface"); list.SetResourceReference(Control.ForegroundProperty, "Text"); list.SetResourceReference(Control.BorderBrushProperty, "Line");
        int offset = 0;
        void Load() {
            if (!InlineUiUnlocked || !ReferenceEquals(wikiChooser, window)) return;
            var notes = page(offset, 50, query.Text); list.ItemsSource = notes.Select(n => new WikiChoice(n.Id, n.Title + " — n/" + app.State.Notebooks.FirstOrDefault(b => b.Id == n.NotebookId)?.Name + " · " + n.Id.ToString("D"))).ToArray();
            message.Text = notes.Count == 0 ? "No notes on this page. Refine search or return to previous page." : $"{offset + 1}–{offset + notes.Count} · select a note, then Choose";
        }
        controls.Children.Add(Btn("Choose", () => { if (!InlineUiUnlocked || !ReferenceEquals(wikiChooser, window) || list.SelectedItem is not WikiChoice choice) return; if (activate(choice.Id)) CloseWikiChooser(); else if (InlineUiUnlocked) message.Text = "Selection changed or navigation cancelled. Refresh search and choose again."; }));
        controls.Children.Add(Btn("Cancel", CloseWikiChooser));
        controls.Children.Add(Btn("Previous", () => { offset = Math.Max(0, offset - 50); Load(); }));
        controls.Children.Add(Btn("Next 50", () => { offset += 50; Load(); }));
        query.TextChanged += (_, _) => { offset = 0; Load(); };
        window.Closed += (_, _) => { query.Clear(); list.ItemsSource = null; if (ReferenceEquals(wikiChooser, window)) { wikiChooser = null; wikiChooserQuery = null; wikiChooserResults = null; } };
        Load(); window.Show(); query.Focus();
    }
    private sealed record WikiChoice(Guid Id, string Label);
    private void CloseWikiChooser()
    {
        var window = wikiChooser; wikiChooser = null;
        wikiChooserQuery?.Clear(); if (wikiChooserResults is not null) wikiChooserResults.ItemsSource = null;
        wikiChooserQuery = null; wikiChooserResults = null; window?.Close();
    }
    private void SuspendWikiLinks(bool closing = false)
    {
        wikiEpoch++; wikiGeneration++; wikiClosed |= closing;
        wikiCancellation?.Cancel(); wikiCancellation?.Dispose(); wikiCancellation = null;
        CloseWikiChooser(); wikiIndex = null; wikiSnapshot = wikiRequestedSnapshot = null;
        wikiBacklinks.Children.Clear(); if (wikiReader is not null) wikiReader.Document = new(); wikiReader = null; wikiReaderId = null;
    }
}



