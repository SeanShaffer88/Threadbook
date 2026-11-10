using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Threadbook.Core;
using Threadbook.Storage;

namespace Threadbook.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly NotebookService app;
    private readonly JsonStore store;
    private readonly RecoveryStore recovery;
    private int feedLimit = 75, replyLimit = 100, renderedReplies;
    private Dictionary<Guid, List<Reply>> replyChildren = new();
    private readonly string dataDir;
    private readonly StackPanel notebooks = new(), feed = new(), detail = new();
    private readonly TextBox search = new(), replyBox = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 85, MaxHeight = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 20000 };
    private readonly TextBlock status = new(), heading = new(), replyTarget = new();
    private readonly Grid columns = new();
    private readonly HashSet<Guid> collapsed = new();
    private readonly DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private Guid? notebookId, selectedId, parentReplyId, editingReplyId;
    private bool savedOnly, narrow, showDetail;
    private NoteSort sort;
    private Border sidebar = null!, feedPanel = null!, detailPanel = null!;
    private Button themeButton = null!, composerSaveButton = null!;
    private readonly TextBlock readingStatus = new();

    public MainWindow(NotebookService app, JsonStore store, string dataDir, bool interactive = true)
    {
        ApplicationBrand.Apply(this);
        this.app = app; this.store = store; this.dataDir = dataDir; recovery = new RecoveryStore(store); archiveMutations = new(store, app);
        Theme.Apply("Dark"); Title = "Threadbook — private notebook"; Width = 1480; Height = 940;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Bg"); SetResourceReference(ForegroundProperty, "Text");
        var root = new DockPanel(); root.SetResourceReference(Panel.BackgroundProperty, "Bg"); Content = root;
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
        var top = new Grid(); var bottom = new Grid();
        bottom.ColumnDefinitions.Add(new()); bottom.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        bottom.Margin = new Thickness(14, 6, 14, 6); status.Text = "Saved locally"; status.FontSize = 12; status.TextTrimming = TextTrimming.CharacterEllipsis; bottom.Children.Add(status);
        readingStatus.Text = "Markdown · Private · Offline"; readingStatus.FontSize = 12; readingStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); Grid.SetColumn(readingStatus, 1); bottom.Children.Add(readingStatus);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        columns.ColumnDefinitions.Add(new() { Width = new GridLength(220) }); columns.ColumnDefinitions.Add(new() { Width = new GridLength(450) }); columns.ColumnDefinitions.Add(new()); root.Children.Add(columns);
        var nav = new StackPanel { Margin = new Thickness(16, 10, 12, 12) };
        nav.Children.Add(Label("NOTEBOOKS", 12, true, "Muted"));
        nav.Children.Add(Nav("All notes", () => Navigate(null, false))); nav.Children.Add(Nav("Saved", () => Navigate(null, true)));
        nav.Children.Add(Nav("History", () => OpenArchive(false))); nav.Children.Add(Nav("Trash", () => OpenArchive(true)));
        nav.Children.Add(notebooks);
        nav.Children.Add(Rule()); nav.Children.Add(Nav("+  Create notebook", CreateNotebook));

        sidebar = PanelBorder(new ScrollViewer { Content = nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); columns.Children.Add(sidebar);
        var middle = new DockPanel { Margin = new Thickness(16, 18, 16, 0) };
        var feedTop = new StackPanel(); heading.FontSize = 22; heading.FontWeight = FontWeights.Bold; feedTop.Children.Add(heading);
        var filters = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) };
        filters.Children.Add(Btn("Sort ↕", () => { if (!MayLeave()) return; sort = (NoteSort)(((int)sort + 1) % 3); RefreshFeed(); }));
        feedTop.Children.Add(filters);
        DockPanel.SetDock(feedTop, Dock.Top); middle.Children.Add(feedTop); middle.Children.Add(new ScrollViewer { Content = feed, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        feedPanel = PanelBorder(middle); Grid.SetColumn(feedPanel, 1); columns.Children.Add(feedPanel);
        var reader = new DockPanel();
        DockPanel.SetDock(workspaceHost, Dock.Top); reader.Children.Add(workspaceHost);
        var right = new DockPanel { Margin = new Thickness(24, 8, 24, 16) }; reader.Children.Add(right);
        var composer = new StackPanel { Margin = new Thickness(0, 14, 0, 0) }; replyTarget.Text = "Add a thought"; replyTarget.Margin = new Thickness(0, 0, 0, 8); composer.Children.Add(replyTarget); composer.Children.Add(replyBox);
        AutomationProperties.SetName(replyBox, "Reply body");
        var replyActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) }; composerSaveButton = Btn("Save reply", () => uiCommands["Save reply"](), true); replyActions.Children.Add(composerSaveButton); replyActions.Children.Add(Btn("Cancel", ClearReply)); composer.Children.Add(replyActions);
        DockPanel.SetDock(composer, Dock.Bottom); right.Children.Add(composer); DockPanel.SetDock(inlineHeader, Dock.Top); right.Children.Add(inlineHeader); DockPanel.SetDock(inlineHost, Dock.Top); right.Children.Add(inlineHost); right.Children.Add(new ScrollViewer { Content = detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        detailPanel = PanelBorder(reader); detailPanel.BorderThickness = new Thickness(0); Grid.SetColumn(detailPanel, 2); columns.Children.Add(detailPanel);
        void FitInlineEditor() {
            if (inlineHost.Children.OfType<ScrollViewer>().FirstOrDefault() is { } editorScroll)
                editorScroll.MaxHeight = Math.Max(70, Math.Min(250, right.ActualHeight - composer.ActualHeight - inlineHeader.ActualHeight - 100));
        }
        right.SizeChanged += (_, _) => FitInlineEditor(); inlineHeader.SizeChanged += (_, _) => FitInlineEditor();
        searchDelay.Tick += (_, _) => { searchDelay.Stop(); if (InlineUiUnlocked) RefreshFeed(); };
        search.TextChanged += SearchChanged; search.SelectionChanged += SearchSelectionChanged;
        SizeChanged += (_, _) => Responsive(); Closing += (_, e) => { if (vaultLocked) return; if (!MayLeave()) e.Cancel = true; else PersistSelectionSession(); };
        PreviewKeyDown += (_, e) =>
        {
            if (CommandPrivacy() is not null) return;
            if (inlineSession?.Surface.IsKeyboardFocusWithin == true && inlineSession.Surface.HandleShortcut(e.Key, Keyboard.Modifiers)) { e.Handled = true; return; }
            if (e.KeyboardDevice.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.P) { OpenCommandPalette(); e.Handled = true; }
            if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.F) { search.Focus(); search.SelectAll(); e.Handled = true; }
            if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.N) { uiCommands["New note"](); e.Handled = true; }
            if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.Enter && replyBox.IsKeyboardFocused) { uiCommands["Save reply"](); e.Handled = true; }
        };
        notebookId = app.State.Notebooks.FirstOrDefault()?.Id;
        if (interactive) RestoreSession();
        InitializeLayouts(root, top, bottom);
        InitializePaneSizing(root);
        InitializeLibrary(root);
        InitializePrivateLibrary(root);
        InitializeCommands(root);
        frame = WindowChromeHelper.Attach(this); frame.CaptionContent = BuildCaption();
        WindowSizing.Attach(this, 1180, 760);
        Refresh();
        if (interactive) Loaded += (_, _) => ReviewRecovery();
        Closed += (_, _) => { search.TextChanged -= SearchChanged; search.SelectionChanged -= SearchSelectionChanged; SuspendWikiLinks(true); draftCoordinator?.Dispose(); inlineSession?.Dispose(); };
    }

    private static TextBlock Label(string text, double size = 14, bool bold = false, string color = "Text")
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 10, 5) }; t.SetResourceReference(TextBlock.ForegroundProperty, color); return t;
    }
    private static Button Btn(string text, Action click, bool primary = false)
    {
        var b = new Button { Content = text, MinHeight = 34 }; AutomationProperties.SetName(b, text); b.Click += (_, _) => click();
        if (primary) { b.SetResourceReference(Control.BackgroundProperty, "Action"); b.Foreground = Brushes.White; b.FontWeight = FontWeights.SemiBold; b.BorderThickness = new Thickness(0); }
        return b;
    }
    private static Button Nav(string text, Action click)
    {
        var b = Btn(text, click); b.HorizontalContentAlignment = HorizontalAlignment.Left; b.Background = Brushes.Transparent; b.Margin = new Thickness(0, 1, 0, 1); b.BorderThickness = new Thickness(0); b.Padding = new Thickness(8, 5, 8, 5); return b;
    }
    private static Border Rule() { var b = new Border { Height = 1, Margin = new Thickness(0, 17, 0, 17) }; b.SetResourceReference(Border.BackgroundProperty, "Line"); return b; }
    private static Border PanelBorder(UIElement child) { var b = new Border { Child = child, BorderThickness = new Thickness(0, 0, 1, 0) }; b.SetResourceReference(Border.BorderBrushProperty, "Line"); return b; }
    private void SetStatus(string message, bool error = false) { status.Text = message; status.ToolTip = message; status.SetResourceReference(TextBlock.ForegroundProperty, error ? "Text" : "Muted"); }
    private bool Run(Command c)
    {
        if (!ArchiveReady()) return false;
        try { return CompleteArchiveMutation(archiveMutations.Execute(c, app.State.Revision, archiveMutations.LibraryState.Revision)); }
        catch (Exception ex) when (ArchiveError(ex)) { SetStatus(ex.Message, true); RefreshArchiveWarning(); return false; }
    }
    private Func<bool>? confirmLeaveForTest;
    private bool MayLeave() => TryLeave(LeaveReason.Navigation);
    private void Navigate(Guid? book, bool saved)
    {
        TryNavigate(book, null, saved);
    }
    private void Refresh()
    {
        if (!InlineUiUnlocked) return;
        notebooks.Children.Clear();
        RenderNotebookTree();
        RefreshFeed();
    }
    private void RefreshFeed()
    {
        if (!InlineUiUnlocked) return;
        heading.Text = savedOnly ? "Saved notes" : notebookId is Guid b ? "n/" + app.State.Notebooks.FirstOrDefault(x => x.Id == b)?.Name : "All notes";
        var list = QueryLibraryNotes();
        feed.Children.Clear(); feed.Children.Add(Label($"{list.Count} notes  ·  {sort} order", 12, false, "Muted"));
        if (selectedId is null || !list.Any(n => n.Id == selectedId))
        {
            // Filtering does not discard a reply draft; it remains visible with its original note.
            if (inlineSession is null && string.IsNullOrWhiteSpace(replyBox.Text)) { ClearReply(); selectedId = list.FirstOrDefault()?.Id; }
        }
        var replyCounts = app.State.Replies.GroupBy(r => r.NoteId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var note in list.Take(feedLimit))
        {
            var content = new StackPanel { Margin = new Thickness(16, 10, 16, 10) };
            content.Children.Add(Label("n/" + app.State.Notebooks.First(b => b.Id == note.NotebookId).Name + "  ·  " + note.Updated.ToLocalTime().ToString("MMM d"), 12, false, "Muted"));
            if (note.Flair.Length > 0) content.Children.Add(Flair(note.Flair));
            content.Children.Add(Label(note.Title, 18, true));
            var excerpt = Label(LibraryExcerpt(note, 180), 14); excerpt.MaxHeight = 44; content.Children.Add(excerpt);
            content.Children.Add(Label($"{replyCounts.GetValueOrDefault(note.Id)} replies" + (note.Saved ? "     ★ Saved" : ""), 12, false, note.Saved ? "Accent" : "Muted"));
            var card = Btn(note.Title, () => SelectNote(note.Id)); card.Content = content; card.HorizontalContentAlignment = HorizontalAlignment.Stretch; card.Padding = new Thickness(0); card.Margin = new Thickness(0); card.BorderThickness = new Thickness(selectedId == note.Id ? 3 : 0, 0, 0, 1);
            card.Background = Brushes.Transparent;
            if (selectedId == note.Id) { card.SetResourceReference(Control.BorderBrushProperty, "Accent"); card.SetResourceReference(Control.BackgroundProperty, "Selected"); } feed.Children.Add(card);
        }
        if(list.Count > feedLimit) feed.Children.Add(Btn($"Load 75 more · {list.Count - feedLimit} remaining", () => { feedLimit += 75; RefreshFeed(); }));
        if (list.Count == 0) feed.Children.Add(Label(search.Text.Length > 0 ? "No matching notes. Search also looks inside replies." : "Nothing here yet. Create a note to start thinking.", 16));
        RefreshDetail();
        RefreshProjection();
    }
    private static Border Flair(string value)
    {
        var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
        b.SetResourceReference(Border.BackgroundProperty, "DeepAccent");
        var caption = new TextBlock { Text = value, FontWeight = FontWeights.SemiBold, FontSize = 12 }; caption.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        b.Child = caption; return b;
    }
    private void SelectNote(Guid id)
    {
        TryNavigate(null, id);
    }
    private void RefreshDetail()
    {
        if (!InlineUiUnlocked) return;
        RequestWikiIndex(); wikiReader = null; wikiReaderId = null; replyElements.Clear(); detail.Children.Clear(); inlineHeader.Children.Clear(); inlineHeader.Visibility = inlineSession is null ? Visibility.Collapsed : Visibility.Visible; var note = app.State.Notes.FirstOrDefault(n => n.Id == selectedId);
        replyBox.IsEnabled = note is not null; UpdateReplyCommand();
        int wordCount = note?.Body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
        readingStatus.Text = wordCount == 0 ? "Markdown · Private · Offline" : "Markdown · " + Math.Max(1, (wordCount + 199) / 200) + " min read · " + wordCount + " words";
        var header = new DockPanel(); var nav = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(nav, Dock.Right); header.Children.Add(nav);
        var back = Btn("← Notes", () => { if (MayLeave()) { showDetail = false; Responsive(); } }); void FitBack() => back.Visibility = Width < 1120 ? Visibility.Visible : Visibility.Collapsed; FitBack(); SizeChanged += FitBackOnSize; back.Unloaded += (_, _) => SizeChanged -= FitBackOnSize; void FitBackOnSize(object sender, SizeChangedEventArgs e) => FitBack(); nav.Children.Add(back);
        if (note is null) { detail.Children.Add(Label("Your thoughts, connected.", 26, true)); detail.Children.Add(Label("Choose a note or create something new.", 16, false, "Muted")); return; }
        nav.Children.Add(IconButton(note.Saved ? "Remove bookmark" : "Bookmark note", "M5,2 L15,2 15,18 10,14 5,18 Z", () => { if (Run(new ToggleSaved(note.Id))) RefreshFeed(); }));
        nav.Children.Add(IconButton("Edit note", "M3,14 L14,3 18,7 7,18 2,19 Z M12,5 L16,9", () => BeginInlineEdit(note.Id)));
        nav.Children.Add(IconButton("Attachments", "M6,13 L13,6 A3,3 0 0 1 17,10 L9,18 A5,5 0 0 1 2,11 L11,2", OpenAttachments));
        var more = Btn("···", () => { }); more.ToolTip = "Note actions"; AutomationProperties.SetName(more, "Note actions");
        var noteMenu = new ContextMenu(); var exportNote = new MenuItem { Header = "Export note…" }; exportNote.Click += (_, _) => ExportNote(note); noteMenu.Items.Add(exportNote);
        var deleteNote = new MenuItem { Header = "Delete note" }; deleteNote.Click += (_, _) => DeleteSelected(note); noteMenu.Items.Add(deleteNote);
        var advanced = new MenuItem { Header = "Open advanced editor…" }; advanced.Click += (_, _) => EditNote(note); noteMenu.Items.Add(advanced);
        var tagsAction = new MenuItem { Header = "Manage tags" }; tagsAction.Click += (_, _) => { if (!vaultLocked) EditNoteTags(note.Id); }; noteMenu.Items.Add(tagsAction);
        var tasksAction = new MenuItem { Header = "Tasks / checklist" }; tasksAction.Click += (_, _) => { if (!vaultLocked) OpenTasks(note); }; noteMenu.Items.Add(tasksAction);
        more.Click += (_, _) => { if (!vaultLocked) { noteMenu.PlacementTarget = more; noteMenu.IsOpen = true; } }; nav.Children.Add(more);
        header.Children.Add(Label("n/" + app.State.Notebooks.First(b => b.Id == note.NotebookId).Name + (note.Flair.Length > 0 ? "  /  " + note.Flair : ""), 12, false, "Muted"));
        if (inlineSession is not null) { inlineHeader.Children.Add(header); inlineHeader.Children.Add(Label(note.Title, 20, true)); }
        else { detail.Children.Add(header); detail.Children.Add(Label(note.Title, 24, true)); }
        detail.Children.Add(Label("n/" + app.State.Notebooks.First(b => b.Id == note.NotebookId).Name + "  ·  Edited " + note.Updated.ToLocalTime().ToString("MMM d, HH:mm"), 12, false, "Muted"));
        if (note.Flair.Length > 0) detail.Children.Add(Flair(note.Flair));
        AddLibraryDetail(note);
        detail.Children.Add(WikiViewer(note)); RefreshWikiBacklinks(); detail.Children.Add(wikiBacklinks); detail.Children.Add(Rule()); detail.Children.Add(Label($"Discussion · {app.State.Replies.Count(r => r.NoteId == note.Id)} replies", 19, true));
        var replies = app.State.Replies.Where(r => r.NoteId == note.Id).OrderBy(r => r.Created).ThenBy(r => r.Id).ToArray();
        replyChildren = replies.Where(r => r.ParentId is not null).GroupBy(r => r.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToList()); renderedReplies = 0;
        foreach (var reply in replies.Where(r => r.ParentId is null)) { if(renderedReplies >= replyLimit) break; AddReply(reply, 0); }
        if(replies.Length > renderedReplies) detail.Children.Add(Btn($"Load more replies / expand branches · {renderedReplies} rendered", () => { replyLimit += 100; RefreshDetail(); }));
        if (!app.State.Replies.Any(r => r.NoteId == note.Id)) detail.Children.Add(Label("Think out loud. Add the first reply below.", 14, false, "Muted"));
    }
    private void AddReply(Reply reply, int depth)
    {
        if(renderedReplies++ >= replyLimit) return;
        var container = new StackPanel { Margin = new Thickness(8, 5, 0, 3), Focusable = true };
        replyElements[reply.Id] = container;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var toggle = IconButton("Toggle reply", collapsed.Contains(reply.Id) ? "M6,3 L13,10 6,17" : "M3,6 L10,13 17,6", () => { if (!collapsed.Add(reply.Id)) collapsed.Remove(reply.Id); RefreshDetail(); }); toggle.Padding = new Thickness(3); toggle.MinHeight = 24; toggle.Width = 24; toggle.Background = Brushes.Transparent;
        AutomationProperties.SetName(toggle, (collapsed.Contains(reply.Id) ? "Expand" : "Collapse") + " reply at depth " + (depth + 1)); row.Children.Add(toggle);
        var avatar = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 8, 0) }; avatar.SetResourceReference(Border.BackgroundProperty, "Hover"); var initial = Label("M", 13, true); initial.Margin = new Thickness(0); initial.HorizontalAlignment = HorizontalAlignment.Center; initial.VerticalAlignment = VerticalAlignment.Center; avatar.Child = initial; row.Children.Add(avatar);
        row.Children.Add(Label("Me", 13, true)); row.Children.Add(Label(reply.Created.ToLocalTime().ToString("MMM d, HH:mm") + (depth >= 5 ? $" · depth {depth + 1}" : ""), 12, false, "Muted")); container.Children.Add(row);
        if (collapsed.Contains(reply.Id)) container.Children.Add(Label("Collapsed branch", 12, false, "Muted"));
        else
        {
            container.Children.Add(Label(reply.Body, 14));
            var actions = new WrapPanel();
            actions.Children.Add(Btn("Reply", () => TargetReply(reply, false))); actions.Children.Add(Btn("Edit", () => TargetReply(reply, true)));
            actions.Children.Add(Btn("Delete", () =>
            {
                var count = app.Descendants(reply.Id).Count;
                if (!MayLeave()) return;
                if (Confirm($"Delete this reply and {count - 1} descendants?")) { if (Run(new DeleteReply(reply.Id))) { ClearReply(); RefreshFeed(); } }
            }));
            foreach (Button action in actions.Children) { action.MinHeight = 24; action.Padding = new Thickness(7, 2, 7, 2); action.FontSize = 12; action.BorderThickness = new Thickness(0); action.Background = Brushes.Transparent; }
            container.Children.Add(actions);
        }
        var guide = new Border { Child = container, BorderThickness = new Thickness(depth > 0 ? 1 : 0, 0, 0, 0), Margin = new Thickness(Math.Min(depth, 5) * 12, 0, 0, 0) }; guide.SetResourceReference(Border.BorderBrushProperty, "Line"); detail.Children.Add(guide);
        if (!collapsed.Contains(reply.Id) && replyChildren.TryGetValue(reply.Id, out var children)) foreach (var child in children) { if(renderedReplies >= replyLimit) break; AddReply(child, depth + 1); }
    }
    private bool Confirm(string message) => MessageBox.Show(this, message + "\nPrior content remains in local backups until they rotate. This cannot be undone here.", "Confirm deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    private void TargetReply(Reply reply, bool edit)
    {
        if (!MayLeaveReply()) return; ClearReply(); parentReplyId = edit ? reply.ParentId : reply.Id; editingReplyId = edit ? reply.Id : null;
        replyTarget.Text = edit ? "Editing your reply" : "Replying to: " + (reply.Body.Length > 50 ? reply.Body[..50] + "…" : reply.Body);
        if (edit) replyBox.Text = reply.Body; replyBox.Focus();
    }
    private void ClearReply() { replyBox.Clear(); parentReplyId = null; editingReplyId = null; replyTarget.Text = "Add a thought"; }
    private void SaveReply()
    {
        if (vaultLocked) return;
        if (selectedId is not Guid id) { SetStatus("Choose a note first.", true); return; }
        if (Run(new PutReply(editingReplyId, id, parentReplyId, replyBox.Text))) { ClearReply(); RefreshFeed(); }
    }
    private void NewNote()
    {
        if (!ArchiveReady()) return;
        if (app.State.Notebooks.Count == 0) { SetStatus("Create a notebook first.", true); CreateNotebook(); return; }
        if (!MayLeave()) return;
        if (!ReviewRecovery()) return;
        var editor = new NoteEditor(this, app.State.Notebooks, null, notebookId, c => Run(c), recovery, app.State.Revision, dataDir, null, saveNote: SaveModalNote);
        if (editor.ShowDialog() == true) { ClearReply(); selectedId = editor.EffectiveNoteId; notebookId = app.State.Notes.Single(n => n.Id == selectedId).NotebookId; savedOnly = false; SetSearch(""); showDetail = true; Refresh(); Responsive(); }
    }
    internal void RequestNewNote() => NewNote();
    private void EditNote(Note note)
    {
        if (!ArchiveReady()) return;
        if (!MayLeave()) return;
        if (!ReviewRecovery()) return;
        var editor = new NoteEditor(this, app.State.Notebooks, note, note.NotebookId, c => Run(c), recovery, app.State.Revision, dataDir, null, saveNote: SaveModalNote);
        if (editor.ShowDialog() == true) { ClearReply(); selectedId = editor.EffectiveNoteId; notebookId = app.State.Notes.Single(n => n.Id == selectedId).NotebookId; Refresh(); }
    }
    private void DeleteSelected(Note note)
    {
        if (!ArchiveReady()) return;
        if (!MayLeave()) return;
        if (Confirm($"Delete “{note.Title}” and {app.State.Replies.Count(r => r.NoteId == note.Id)} replies?"))
            if (Run(new DeleteNote(note.Id))) { ClearReply(); selectedId = null; Refresh(); }
    }
    private void CreateNotebook()
    {
        if (!ArchiveReady()) return;
        if (!MayLeave()) return;
        var form = new TextEditor(this, "Create notebook", "Notebook name", "", 60, value => Run(new PutNotebook(null, value)));
        if (form.ShowDialog() == true) { ClearReply(); notebookId = app.State.Notebooks.Last().Id; savedOnly = false; Refresh(); }
    }
    private void ManageNotebook(Notebook book)
    {
        if (!ArchiveReady()) return;
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "Rename notebook" }; rename.Click += (_, _) => { if (!ArchiveReady()) return; var form = new TextEditor(this, "Rename notebook", "Notebook name", book.Name, 60, v => Run(new PutNotebook(book.Id, v))); if (form.ShowDialog() == true) Refresh(); };
        var delete = new MenuItem { Header = "Delete notebook…" }; delete.Click += (_, _) =>
        {
            if (!ArchiveReady() || !MayLeave()) return;
            var ids = app.State.Notes.Where(n => n.NotebookId == book.Id).Select(n => n.Id).ToHashSet();
            if (Confirm($"Delete n/{book.Name}, {ids.Count} notes and {app.State.Replies.Count(r => ids.Contains(r.NoteId))} replies?"))
                if (Run(new DeleteNotebook(book.Id))) { ClearReply(); if (notebookId == book.Id) notebookId = null; selectedId = null; Refresh(); }
        };
        menu.Items.Add(rename); menu.Items.Add(delete); menu.IsOpen = true;
    }
    private void PickNotebook()
    {
        var menu = new ContextMenu();
        foreach (var b in app.State.Notebooks) { var item = new MenuItem { Header = "n/" + b.Name }; item.Click += (_, _) => Navigate(b.Id, false); menu.Items.Add(item); }
        menu.IsOpen = true;
    }
    private bool IsStorePath(string path) => ExportSafety.IsProtected(path, dataDir);
    private void Export()
    {
        var ticket = CapturePrivateExportTicket();
        if (!ConfirmPrivatePlaintextExport()) return;
        var dialog = new SaveFileDialog { Filter = "Threadbook JSON|*.json", FileName = "Threadbook-export.json", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        if (IsStorePath(dialog.FileName)) { SetStatus("Choose an export location outside the live data folder.", true); return; }
        try { if (CompletePrivateExport(ticket, () => JsonStore.Export(app.State, dialog.FileName, true))) SetStatus("Exported complete notebook snapshot."); }
        catch (Exception ex) when (ex is DataException or IOException or UnauthorizedAccessException) { SetStatus("Export failed: " + ex.Message, true); }
    }
    private void Import()
    {
        if (!ArchiveReady()) return;
        if (!MayLeave()) return;
        var dialog = new OpenFileDialog { Filter = "Threadbook JSON|*.json" }; if (dialog.ShowDialog(this) != true) return;
        if (!TryLeave(LeaveReason.NewNote)) return;
        try
        {
            var preview = NoteImports.PrepareJsonAppend(JsonStore.Read(dialog.FileName), app.State);
            if (MessageBox.Show(this, $"Append imported content, producing {preview.Notebooks.Count} notebooks, {preview.Notes.Count} notes, and {preview.Replies.Count} replies?\nExisting content is preserved; imported IDs are remapped.", "Import preview", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (Run(new ReplaceAll(preview))) { ClearReply(); selectedId = null; notebookId = null; Refresh(); }
        }
        catch (Exception ex) when (ex is DataException or IOException or UnauthorizedAccessException) { SetStatus("Import rejected: " + ex.Message, true); }
    }
    private void CycleTheme() { Theme.Apply(Theme.Mode switch { "Dark" => "Light", "Light" => "System", _ => "Dark" }); themeButton.Content = "Theme: " + Theme.Mode; }
    private void Responsive()
    {
        if (ApplyLayoutSizing()) { ApplyPaneSizing(); return; }
        narrow = ActualWidth < 1120;
        sidebar.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible; columns.ColumnDefinitions[0].Width = new GridLength(narrow ? 0 : 240);
        feedPanel.Visibility = narrow && showDetail ? Visibility.Collapsed : Visibility.Visible;
        detailPanel.Visibility = narrow && !showDetail ? Visibility.Collapsed : Visibility.Visible;
        columns.ColumnDefinitions[1].Width = narrow ? new GridLength(showDetail ? 0 : 1, showDetail ? GridUnitType.Pixel : GridUnitType.Star) : new GridLength(390);
        columns.ColumnDefinitions[2].Width = narrow && !showDetail ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ApplyPaneSizing();
    }
    private void RestoreSession()
    {
        try { var session = recovery.LoadSession(); if(session is null) return; if(app.State.Notebooks.Any(b => b.Id == session.NotebookId)) notebookId=session.NotebookId; if(app.State.Notes.Any(n => n.Id == session.NoteId && n.NotebookId == notebookId)) selectedId=session.NoteId; }
        catch(DataException ex) { SetStatus("Session unavailable: " + ex.Message, true); }
    }
    private void PersistSelectionSession()
    {
        try { var prior = recovery.LoadSession(); var caret = prior is not null && prior.NoteId == selectedId ? prior.Caret : 0;
            var result = recovery.SaveSession(new RecoverySession(1, selectedId is null ? notebookId : app.State.Notes.FirstOrDefault(n => n.Id == selectedId)?.NotebookId, selectedId, caret, DateTimeOffset.UtcNow));
            if(!result.Success) SetStatus("Session not retained: " + result.Error!.Message, true);
        } catch(DataException ex) { SetStatus("Session preserved: " + ex.Message, true); }
    }
    private bool ReviewRecovery()
    {
        if (!ArchiveReady() || (inlineSession is not null && !TryLeave(LeaveReason.ModalEditor))) return false;
        RecoveryDraft? draft;
        try { draft = recovery.LoadDraft(); }
        catch(DataException ex) { SetStatus("Recovery sidecar preserved: " + ex.Message, true); var decision = MessageBox.Show(this, "Recovery sidecar cannot be read. Yes: discard it. No: export its original bytes. Cancel: keep it unchanged.", "Recovery unavailable", MessageBoxButton.YesNoCancel); if(decision == MessageBoxResult.Yes) return recovery.DiscardDraft().Success;
            if(decision == MessageBoxResult.No) { var save = new SaveFileDialog { Filter="JSON|*.json", FileName="Threadbook-recovery-preserved.json", OverwritePrompt=true }; if(save.ShowDialog(this)==true) try { ExportSafety.CopyPreserved(recovery.DraftPath,save.FileName,dataDir); } catch(Exception error) when(error is IOException or UnauthorizedAccessException) { SetStatus(error.Message,true); } } return false; }
        if(draft is null) return true;
        var original = app.State.Notes.FirstOrDefault(n => n.Id == draft.NoteId);
        string warning = draft.BaseRevision != app.State.Revision ? "\nSaved content has changed since this draft began. Review before Save." : "";
        if(draft.NoteId is not null && original is null) warning += "\nThe original note was deleted. Restoring creates a new note.";
        var choice = MessageBox.Show(this, "An unsaved note draft is available." + warning + "\nYes: restore and review. No: discard draft. Cancel: keep for later.", "Recover unsaved draft", MessageBoxButton.YesNoCancel);
        if(choice == MessageBoxResult.No) return recovery.DiscardDraft().Success;
        if(choice != MessageBoxResult.Yes) return false;
        var editor = new NoteEditor(this, app.State.Notebooks, original, draft.NotebookId, c => Run(c), recovery, app.State.Revision, dataDir, draft, saveNote: SaveModalNote);
        if (editor.ShowDialog() == true && editor.EffectiveNoteId is Guid savedId) {
            var saved = app.State.Notes.Single(n => n.Id == savedId);
            ClearReply(); selectedId = saved.Id; notebookId = saved.NotebookId;
        }
        Refresh(); return false;
    }
    private void ExportNote(Note note)
    {
        var ticket = CapturePrivateExportTicket();
        if (!ConfirmPrivatePlaintextExport()) return;
        var dialog = new SaveFileDialog { Filter = "Markdown|*.md|HTML|*.html", FileName = "Threadbook-note.md", OverwritePrompt = true };
        if(dialog.ShowDialog(this) != true) return;
        try { if (CompletePrivateExport(ticket, () => ExportSafety.Write(dialog.FileName, dataDir, dialog.FilterIndex == 2 ? Markdown.ExportHtml(note.Title, note.Body) : note.Body))) SetStatus("Exported saved note. Use Export draft in the editor for unsaved changes."); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or DataException) { SetStatus("Export failed: " + ex.Message, true); }
    }
    public void Capture(string path) => CaptureVisual(this, path);
    internal static void CaptureVisual(Window window, string path)
    {
        window.UpdateLayout(); var visual = new DrawingVisual();
        using(var dc = visual.RenderOpen()) { dc.DrawRectangle(window.Background, null, new Rect(0,0,window.ActualWidth,window.ActualHeight)); }
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Render((Visual)window.Content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); png.Save(file);
    }
    public async System.Threading.Tasks.Task RunSmokeTest()
    {
        void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        var book = app.State.Notebooks.First();
        // Use the same form save path as the interactive editor, without showing modal dialogs.
        var form = new NoteEditor(this, app.State.Notebooks, null, book.Id, c => Run(c), recovery, app.State.Revision, dataDir, null, saveNote: SaveModalNote);
        Assert(form.SaveForTest("Smoke note", "Original body", "Test"), "Create via editor");
        selectedId = app.State.Notes.Last().Id; notebookId = book.Id; Refresh();
        replyBox.Text = "Root branch search-token"; SaveReply(); Assert(replyBox.Text.Length == 0, "Reply committed"); var root = app.State.Replies.Last();
        TargetReply(root, false); replyBox.Text = "Nested thought"; SaveReply(); Assert(app.State.Replies.Last().ParentId == root.Id, "Nested parent");
        TargetReply(root, true); replyBox.Text = "Edited root search-token"; SaveReply(); Assert(app.State.Replies.Single(r => r.Id == root.Id).Body.StartsWith("Edited"), "Edit reply");
        var timestamp = root.Created;
        var low = new Reply(Guid.Parse("00000000-0000-0000-0000-000000000001"), root.NoteId, root.Id, "GUID first", timestamp, timestamp);
        var high = low with { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Body = "GUID second" };
        Assert(Run(new ReplaceAll(app.State with { Replies = app.State.Replies.Concat(new[] { high, low }).ToArray() })), "Import reverse GUID siblings"); RefreshDetail();
        var rendered = detail.Children.OfType<Border>().Select(p => p.Child).OfType<StackPanel>().SelectMany(p => p.Children.OfType<TextBlock>()).Select(t => t.Text).Where(t => t.StartsWith("GUID ")).ToArray();
        Assert(rendered.SequenceEqual(app.Children(root.NoteId, root.Id).Where(r => r.Id == low.Id || r.Id == high.Id).Select(r => r.Body)) && rendered.SequenceEqual(new[] { low.Body, high.Body }), "Rendered GUID sibling order matches core");
        collapsed.Add(root.Id); RefreshDetail(); collapsed.Clear(); RefreshDetail();
        search.Text = "search-token"; RefreshFeed(); Assert(app.Query(new(Search: search.Text)).Count == 1, "Reply search"); SetSearch("");
        Assert(Run(new ToggleSaved(selectedId.Value)), "Save flag");
        replyBox.Text = "Draft must survive"; var rev = app.State.Revision; store.BeforeReplace = () => throw new IOException("Controlled smoke failure"); SaveReply();
        Assert(replyBox.Text == "Draft must survive" && app.State.Revision == rev, "Failed commit retained draft"); store.BeforeReplace = null; Assert(CompleteArchiveMutation(archiveMutations.ReconcilePending()), "Explicit completion after interrupted commit"); ClearReply();
        var export = Path.Combine(Path.GetDirectoryName(dataDir)!, "smoke-export-" + Guid.NewGuid().ToString("N") + ".json"); JsonStore.Export(app.State, export, false); var preview = JsonStore.Read(export); Assert(Run(new ReplaceAll(preview)), "Import: " + status.Text);
        var note = app.State.Notes.Single(n => n.Id == selectedId); var edit = new NoteEditor(this, app.State.Notebooks, note, book.Id, c => Run(c), recovery, app.State.Revision, dataDir, null, saveNote: SaveModalNote); Assert(edit.SaveForTest("Smoke edited", "Changed body", "Updated"), "Edit via form");
        Assert(Run(new DeleteReply(root.Id)), "Delete subtree"); Assert(!app.State.Replies.Any(r => r.Id == root.Id || r.ParentId == root.Id) && app.State.Replies.Single().Body == "Draft must survive", "Reply cascade preserves explicitly completed unrelated draft"); Assert(Run(new DeleteNote(note.Id)), "Delete note");
        Assert(Run(new PutNotebook(null, "Smoke notebook")), "Create notebook"); var other = app.State.Notebooks.Last(); Assert(Run(new PutNotebook(other.Id, "Renamed smoke")), "Rename"); Assert(Run(new DeleteNotebook(other.Id)), "Delete notebook");
        Assert(Run(new PutNote(null, book.Id, "Context owner", "owner", "")), "Context owner"); var ownerNote = app.State.Notes.Last(); selectedId=ownerNote.Id;
        replyBox.Text="context"; SaveReply(); TargetReply(app.State.Replies.Last(), true); replyBox.Clear();
        Assert(Run(new PutNote(null, book.Id, "Other context", "other", "")), "Other note"); search.Text="Other context"; RefreshFeed();
        Assert(editingReplyId is null && parentReplyId is null, "Filter clears empty reply context"); SetSearch(""); ClearReply();
        foreach(var contextNote in app.State.Notes.Where(n=>n.Title is "Context owner" or "Other context").ToArray()) Assert(Run(new DeleteNote(contextNote.Id)), "Clean context fixture");
        var failedEditor = new NoteEditor(this, app.State.Notebooks, null, book.Id, c=>Run(c), recovery, app.State.Revision, dataDir, null, saveNote: SaveModalNote); var beforeFailedEdit=app.State.Revision;
        store.BeforeReplace=()=>throw new IOException("Controlled editor failure"); Assert(!failedEditor.SaveForTest("Failed editor draft","retain editor body",""), "Editor save fails"); store.BeforeReplace=null;
        Assert(app.State.Revision==beforeFailedEdit && recovery.LoadDraft()?.Body=="retain editor body", "Failed editor retains draft and canonical state"); failedEditor.Close(); recovery.DiscardDraft(); Assert(CompleteArchiveMutation(archiveMutations.CancelPending()), "Explicit cancel of failed editor preparation");
        var writing = new NoteEditor(this, app.State.Notebooks, null, book.Id, c => Run(c), recovery, app.State.Revision, dataDir, null, saveNote: SaveModalNote);
        writing.Show(); await System.Threading.Tasks.Task.Delay(120); writing.WritingSmoke(Path.Combine(dataDir,"editor-preview.png")); writing.Close();
        Assert(recovery.LoadDraft() is not null, "Recovery remains after accepted close"); recovery.DiscardDraft();
        Assert(ExportSafety.IsProtected(store.PrimaryPath, dataDir) && ExportSafety.IsProtected(Path.Combine(dataDir,"backup-test.json"),dataDir), "Protected exports");
        CycleTheme(); CycleTheme(); CycleTheme(); Demo.Seed(app); selectedId = null; Refresh();
        var rootReply = app.State.Replies.First(r => r.ParentId is null); collapsed.Add(rootReply.Id); RefreshDetail(); Capture(Path.Combine(dataDir, "smoke-collapsed.png")); collapsed.Clear();
        Theme.Apply("Light"); Refresh(); Capture(Path.Combine(dataDir, "smoke-light.png")); Theme.Apply("Dark");
        Width = 900; Height = 740; showDetail = false; await System.Threading.Tasks.Task.Delay(120); Responsive(); UpdateLayout(); Capture(Path.Combine(dataDir, "smoke-narrow-feed.png"));
        showDetail = true; Responsive(); UpdateLayout(); Capture(Path.Combine(dataDir, "smoke-narrow-detail.png"));
        Width = 1480; Height = 940; await System.Threading.Tasks.Task.Delay(120); Responsive(); Refresh();
    }
}
