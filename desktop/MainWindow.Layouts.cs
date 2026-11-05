using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Threadbook.Core;

namespace Threadbook.Desktop;

// Presentation projections share the canonical query, selection and conversation controls.
public sealed partial class MainWindow
{
    private LayoutPreferenceStore preferences = null!;
    private LayoutPreferenceState layoutState = LayoutPreferenceState.Default;
    private readonly ComboBox layoutPicker = new() { MinWidth = 190, MinHeight = 34 };
    private readonly DockPanel projection = new();
    private readonly WrapPanel workspaceBar = new();
    private readonly ScrollViewer workspaceHost = new() { MaxHeight = 94, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock preferenceWarning = new() { TextWrapping = TextWrapping.Wrap };
    private Border projectionPanel = null!, warningPanel = null!;
    private bool changingLayout, renderingProjection;
    private int mapLimit = 250;
    private double mapZoom = 1;
    private readonly Dictionary<Guid, FrameworkElement> replyElements = new();
    private static readonly string[] LayoutNames = ["Original Threads", "Kanban Flow", "Mind Map Central", "Magazine Minimal", "Focus Editor", "Workspace Tabs"];

    private void InitializeLayouts(DockPanel root, Grid top, Grid bottom)
    {
        preferences = new LayoutPreferenceStore(store);
        var loaded = preferences.Load(app.State); layoutState = loaded.State;
        layoutPicker.Style = (Style)Application.Current.Resources[typeof(ComboBox)]; layoutPicker.ItemsSource = LayoutNames;
        AutomationProperties.SetName(layoutPicker, "Notebook layout");
        layoutPicker.SetResourceReference(Control.ForegroundProperty, "Text");
        var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetResourceReference(TextBlock.ForegroundProperty, "Text"); text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        layoutPicker.ItemTemplate = new DataTemplate { VisualTree = text };
        layoutPicker.SelectedIndex = (int)layoutState.Layout;
        workspaceHost.Content = workspaceBar; DockPanel.SetDock(workspaceHost, Dock.Top);

        // The existing workspace host is composed into the reader pane.
        var warning = new DockPanel { Margin = new Thickness(14, 4, 14, 4) };
        var reset = Btn("Reset preferences", () => {
            if (MessageBox.Show(this, "Preserve the current preferences file and reset layout settings? Notes and replies are unchanged.", "Reset preferences", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var result = preferences.Reset(app.State);
            if (result.Success) { layoutState = LayoutPreferenceState.Default; preferenceWarning.Text = ""; ChangeLayout(NotebookLayout.Original); }
            else preferenceWarning.Text = result.Error!.Message;
            UpdatePreferenceWarning();
        });
        DockPanel.SetDock(reset, Dock.Right); warning.Children.Add(reset);
        var retry = Btn("Retry saving", SavePreferences); DockPanel.SetDock(retry, Dock.Right); warning.Children.Add(retry);
        warning.Children.Add(preferenceWarning); warningPanel = new Border { Child = warning }; warningPanel.SetResourceReference(Border.BackgroundProperty, "Selected");
        DockPanel.SetDock(warningPanel, Dock.Top); root.Children.Insert(root.Children.Count - 1, warningPanel);
        preferenceWarning.Text = loaded.Warning ?? ""; UpdatePreferenceWarning();
        projectionPanel = PanelBorder(projection); Grid.SetColumn(projectionPanel, 1); columns.Children.Add(projectionPanel);
        layoutPicker.SelectionChanged += (_, _) => { if (!changingLayout && layoutPicker.SelectedIndex >= 0) ChangeLayout((NotebookLayout)layoutPicker.SelectedIndex); };
        Closing += (_, e) => { if (!vaultLocked && !e.Cancel) { CaptureWorkspace(); SavePreferences(); } };
        if (layoutState.ActiveWorkspaceId is Guid active) {
            var context = layoutState.Workspaces.FirstOrDefault(x => x.Id == active);
            if (context is not null) RestoreWorkspace(context);
        }
    }
    private void UpdatePreferenceWarning() => warningPanel.Visibility = string.IsNullOrEmpty(preferenceWarning.Text) ? Visibility.Collapsed : Visibility.Visible;
    private void OpenNotebookManagement()
    {
        var menu = new ContextMenu();
        var create = new MenuItem { Header = new TextBlock { Text = "+ Create notebook", Foreground = (Brush)Application.Current.Resources["Text"] } }; create.Click += (_, _) => CreateNotebook(); menu.Items.Add(create);
        foreach (var book in app.State.Notebooks) {
            var item = new MenuItem { Header = new TextBlock { Text = "Manage n/" + book.Name, Foreground = (Brush)Application.Current.Resources["Text"] } }; item.Click += (_, _) => ManageNotebook(book); menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
    private void SavePreferences()
    {
        var result = preferences.Save(layoutState, app.State);
        preferenceWarning.Text = result.Success ? "" : "UI preferences not retained: " + result.Error!.Message;
        UpdatePreferenceWarning();
    }
    private void ChangeLayout(NotebookLayout layout)
    {
        if (!MayLeave()) { changingLayout = true; layoutPicker.SelectedIndex = (int)layoutState.Layout; changingLayout = false; return; }
        CaptureWorkspace(); layoutState = layoutState with { Layout = layout };
        changingLayout = true; layoutPicker.SelectedIndex = (int)layout; changingLayout = false;
        RefreshDetail(); RefreshProjection(); Responsive(); SavePreferences();
    }
    private bool ApplyLayoutSizing()
    {
        if (projectionPanel is null) return false;
        bool original = layoutState.Layout == NotebookLayout.Original;
        projectionPanel.Visibility = original || layoutState.Layout is NotebookLayout.FocusEditor or NotebookLayout.WorkspaceTabs ? Visibility.Collapsed : Visibility.Visible;
        workspaceHost.Visibility = layoutState.Layout is NotebookLayout.Original or NotebookLayout.WorkspaceTabs ? Visibility.Visible : Visibility.Collapsed;
        if (original) return false;
        narrow = ActualWidth < 1000;
        bool lists = layoutState.Layout is NotebookLayout.FocusEditor or NotebookLayout.WorkspaceTabs;
        sidebar.Visibility = Visibility.Collapsed; columns.ColumnDefinitions[0].Width = new GridLength(0);
        feedPanel.Visibility = lists && !(narrow && showDetail) ? Visibility.Visible : Visibility.Collapsed;
        projectionPanel.Visibility = !lists && !(narrow && showDetail) ? Visibility.Visible : Visibility.Collapsed;
        detailPanel.Visibility = narrow && !showDetail ? Visibility.Collapsed : Visibility.Visible;
        columns.ColumnDefinitions[1].Width = narrow ? new GridLength(showDetail ? 0 : 1, showDetail ? GridUnitType.Pixel : GridUnitType.Star)
            : lists ? new GridLength(layoutState.Layout == NotebookLayout.FocusEditor ? 285 : 350) : new GridLength(1, GridUnitType.Star);
        columns.ColumnDefinitions[2].Width = narrow ? new GridLength(showDetail ? 1 : 0, showDetail ? GridUnitType.Star : GridUnitType.Pixel)
            : lists ? new GridLength(1, GridUnitType.Star) : new GridLength(430);
        return true;
    }
    private void RefreshProjection()
    {
        if (projectionPanel is null || renderingProjection) return;
        renderingProjection = true;
        try {
            projection.Children.Clear(); RenderWorkspaceBar();
            if (layoutState.Layout is NotebookLayout.Original or NotebookLayout.WorkspaceTabs) return;
            if (layoutState.Layout == NotebookLayout.FocusEditor) {
                if (selectedId is Guid id && app.State.Notes.FirstOrDefault(n => n.Id == id) is Note note) {
                    var edit = Btn("Open advanced editor · Markdown writer", () => EditNote(note), true); detail.Children.Insert(0, edit);
                }
                return;
            }
            var title = new StackPanel { Margin = new Thickness(18, 14, 18, 8) };
            title.Children.Add(Label(heading.Text, 25, true));
            title.Children.Add(Label(LayoutNames[(int)layoutState.Layout], 13, false, "Muted"));
            DockPanel.SetDock(title, Dock.Top); projection.Children.Add(title);
            var notes = QueryLibraryNotes();
            if (layoutState.Layout == NotebookLayout.KanbanFlow) RenderBoard(notes);
            else if (layoutState.Layout == NotebookLayout.MagazineMinimal) RenderMagazine(notes);
            else RenderMap(notes);
        } finally { renderingProjection = false; }
    }
    private Button ProjectedCard(Note note, double width = double.NaN)
    {
        var content = new StackPanel(); content.Children.Add(Label(note.Title, 17, true));
        content.Children.Add(Label("n/" + app.State.Notebooks.First(b => b.Id == note.NotebookId).Name, 12, false, "Muted"));
        var excerpt = Label(LibraryExcerpt(note, 150), 13); excerpt.MaxHeight = 76; content.Children.Add(excerpt);
        content.Children.Add(Label($"{app.State.Replies.Count(r => r.NoteId == note.Id)} replies" + (note.Saved ? " · ★ Saved" : ""), 12, false, "Muted"));
        var card = Btn(note.Title, () => SelectNote(note.Id)); card.Content = content; card.Width = width; card.Margin = new Thickness(5); card.Padding = new Thickness(12);
        if (note.Id == selectedId) card.SetResourceReference(Control.BorderBrushProperty, "Accent");
        return card;
    }
    private void MoveCard(Guid id, BoardLane lane)
    {
        var lanes = layoutState.Lanes.ToDictionary(x => x.Key, x => x.Value); lanes[id] = lane;
        layoutState = layoutState with { Lanes = lanes }; SavePreferences(); RefreshProjection();
    }
    private void RenderBoard(IReadOnlyList<Note> notes)
    {
        var board = new Grid { Margin = new Thickness(10, 0, 10, 12) };
        foreach (var lane in Enum.GetValues<BoardLane>()) {
            board.ColumnDefinitions.Add(new()); var laneNotes = notes.Where(n => layoutState.Lanes.GetValueOrDefault(n.Id) == lane).ToArray();
            var panel = new DockPanel { Margin = new Thickness(3) };
            var caption = Label(lane switch { BoardLane.InProgress => "In progress", _ => lane.ToString() } + $" · {laneNotes.Length}", 16, true);
            DockPanel.SetDock(caption, Dock.Top); panel.Children.Add(caption); var cards = new StackPanel();
            foreach (var note in laneNotes.Take(feedLimit)) {
                var item = new StackPanel(); item.Children.Add(ProjectedCard(note));
                var move = Btn("Move to…", () => { }); AutomationProperties.SetName(move, "Move " + note.Title + " to lane"); var menu = new ContextMenu();
                foreach (var target in Enum.GetValues<BoardLane>()) { var entry = new MenuItem { Header = new TextBlock { Text = target == BoardLane.InProgress ? "In progress" : target.ToString(), Foreground = (Brush)Application.Current.Resources["Text"] }, IsCheckable = true, IsChecked = target == lane }; entry.Click += (_, _) => MoveCard(note.Id, target); menu.Items.Add(entry); }
                move.Click += (_, _) => { menu.PlacementTarget = move; menu.IsOpen = true; }; item.Children.Add(move); cards.Children.Add(item);
            }
            if (laneNotes.Length > feedLimit) cards.Children.Add(Btn($"Load more · {laneNotes.Length - feedLimit} remaining", () => { feedLimit += 75; RefreshProjection(); }));
            panel.Children.Add(new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Grid.SetColumn(panel, (int)lane); board.Children.Add(panel);
        }
        projection.Children.Add(board);
    }
    private void RenderMagazine(IReadOnlyList<Note> notes)
    {
        var cards = new WrapPanel { Margin = new Thickness(10) };
        foreach (var note in notes.Take(feedLimit)) cards.Children.Add(ProjectedCard(note, Math.Max(210, Math.Min(330, (Math.Max(460, projectionPanel.ActualWidth) - 50) / 2))));
        if (notes.Count > feedLimit) cards.Children.Add(Btn($"Load more · {notes.Count - feedLimit} remaining", () => { feedLimit += 75; RefreshProjection(); }));
        projection.Children.Add(new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
    }
    private void RenderMap(IReadOnlyList<Note> notes)
    {
        var controls = new WrapPanel { Margin = new Thickness(12, 0, 12, 8) }; DockPanel.SetDock(controls, Dock.Top); projection.Children.Add(controls);
        var canvas = new Canvas { LayoutTransform = new ScaleTransform(mapZoom, mapZoom), Background = Brushes.Transparent };
        var scroll = new ScrollViewer { Content = canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, CanContentScroll = false };
        void Zoom(double value) { mapZoom = Math.Clamp(value, .25, 2); canvas.LayoutTransform = new ScaleTransform(mapZoom, mapZoom); }
        controls.Children.Add(Btn("−", () => Zoom(mapZoom - .15))); controls.Children.Add(Btn("+", () => Zoom(mapZoom + .15)));
        controls.Children.Add(Btn("Fit", () => Zoom(Math.Min(scroll.ViewportWidth / Math.Max(canvas.Width, 1), scroll.ViewportHeight / Math.Max(canvas.Height, 1)))));
        controls.Children.Add(Btn("←", () => scroll.LineLeft())); controls.Children.Add(Btn("→", () => scroll.LineRight()));
        controls.Children.Add(Btn("↑", () => scroll.LineUp())); controls.Children.Add(Btn("↓", () => scroll.LineDown()));
        Point? drag = null; double sx = 0, sy = 0;
        canvas.MouseDown += (_, e) => { if (e.ChangedButton != MouseButton.Middle) return; drag = e.GetPosition(scroll); sx = scroll.HorizontalOffset; sy = scroll.VerticalOffset; canvas.CaptureMouse(); e.Handled = true; };
        canvas.MouseMove += (_, e) => { if (drag is Point start) { var current = e.GetPosition(scroll); scroll.ScrollToHorizontalOffset(sx + start.X - current.X); scroll.ScrollToVerticalOffset(sy + start.Y - current.Y); } };
        canvas.MouseUp += (_, _) => { drag = null; canvas.ReleaseMouseCapture(); };
        AutomationProperties.SetName(scroll, "Notebook mind map; arrow buttons pan, middle mouse drag pans");
        int count = 0, row = 0, maxDepth = 0;
        var children = app.State.Replies.OrderBy(r => r.Created).ThenBy(r => r.Id).GroupBy(r => r.ParentId ?? Guid.Empty).ToDictionary(g => g.Key, g => g.ToArray());
        var noteReplies = app.State.Replies.Where(r => r.ParentId is null).GroupBy(r => r.NoteId).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Created).ThenBy(r => r.Id).ToArray());
        var positions = new Dictionary<Guid, Point>();
        void Node(Guid id, Guid? parent, int depth, string text, Action action) {
            double x = 20 + depth * 210, y = 15 + row++ * 65; maxDepth = Math.Max(maxDepth, depth); var point = new Point(x, y + 22);
            if (parent is Guid p && positions.TryGetValue(p, out var from)) {
                var path = new Path { StrokeThickness = 1.2, IsHitTestVisible = false }; path.SetResourceReference(Shape.StrokeProperty, "Muted");
                var geometry = new PathGeometry(); var figure = new PathFigure { StartPoint = new Point(from.X + 180, from.Y) };
                figure.Segments.Add(new BezierSegment(new Point(from.X + 200, from.Y), new Point(x - 30, point.Y), point, true)); geometry.Figures.Add(figure); path.Data = geometry; canvas.Children.Add(path);
            }
            positions[id] = point; var button = Btn(text, action); button.Width = 180; button.Height = 46; button.Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, MaxHeight = 32 };
            if (search.Text.Length > 0 && libraryHits.TryGetValue(id, out var hit)) button.ToolTip = hit.Source + ": " + hit.Snippet;
            Canvas.SetLeft(button, x); Canvas.SetTop(button, y); canvas.Children.Add(button); count++;
        }
        foreach (var book in app.State.Notebooks.Where(b => notes.Any(n => n.NotebookId == b.Id)).OrderBy(b => b.Created)) {
            if (count >= mapLimit) break; Node(book.Id, null, 0, "n/" + book.Name, () => Navigate(book.Id, false));
            foreach (var note in notes.Where(n => n.NotebookId == book.Id)) {
                if (count >= mapLimit) break; Node(note.Id, book.Id, 1, note.Title, () => SelectNote(note.Id));
                var queue = new Stack<(Reply Reply, int Depth)>();
                if (noteReplies.TryGetValue(note.Id, out var roots)) foreach (var reply in roots.Reverse()) queue.Push((reply, 2));
                while (queue.Count > 0 && count < mapLimit) {
                    var (reply, depth) = queue.Pop(); Node(reply.Id, reply.ParentId ?? note.Id, depth, reply.Body, () => OpenMapReply(reply));
                    if (children.TryGetValue(reply.Id, out var nested)) foreach (var child in nested.Reverse()) queue.Push((child, depth + 1));
                }
            }
        }
        var noteIds = notes.Select(n => n.Id).ToHashSet();
        int total = notes.Count + notes.Select(n => n.NotebookId).Distinct().Count() + app.State.Replies.Count(r => noteIds.Contains(r.NoteId));
        if (total > count) controls.Children.Add(Btn($"Load 250 more · {total - count} omitted", () => { mapLimit += 250; RefreshProjection(); }));
        canvas.Width = Math.Max(500, (maxDepth + 1) * 210 + 20); canvas.Height = Math.Max(300, row * 65 + 20); projection.Children.Add(scroll);
    }
    private void OpenMapReply(Reply reply)
    {
        SelectNote(reply.NoteId); if (selectedId != reply.NoteId) return;
        var byId = app.State.Replies.ToDictionary(r => r.Id); var cursor = reply;
        while (cursor.ParentId is Guid p && byId.TryGetValue(p, out var parent)) { collapsed.Remove(p); cursor = parent; }
        replyLimit = Math.Max(replyLimit, app.State.Replies.Count(r => r.NoteId == reply.NoteId)); RefreshDetail();
        if (replyElements.TryGetValue(reply.Id, out var element)) { element.SetResourceReference(Panel.BackgroundProperty, "Selected"); element.BringIntoView(); element.Focus(); }
    }
    private void CaptureWorkspace()
    {
        if (layoutState.ActiveWorkspaceId is not Guid id) return;
        var tabs = layoutState.Workspaces.Select(t => t.Id != id ? t : t with { NotebookId = t.NoteId is Guid noteId ? app.State.Notes.FirstOrDefault(n => n.Id == noteId)?.NotebookId : notebookId, SavedOnly = savedOnly, Search = search.Text[..Math.Min(500, search.Text.Length)], Sort = sort, SelectedNoteId = t.NoteId ?? selectedId }).ToArray();
        layoutState = layoutState with { Workspaces = tabs };
    }
    private void RestoreWorkspace(WorkspaceContext tab)
    {
        notebookId = tab.NotebookId; savedOnly = tab.SavedOnly; sort = tab.Sort; selectedId = tab.NoteId ?? tab.SelectedNoteId; SetSearch(tab.Search);
    }
    private void ActivateWorkspace(Guid id, bool alreadyConfirmed = false)
    {
        if (layoutState.ActiveWorkspaceId == id) return;
        if (!alreadyConfirmed && !MayLeave()) return; ClearReply(); CaptureWorkspace();
        var tab = layoutState.Workspaces.First(t => t.Id == id); layoutState = layoutState with { ActiveWorkspaceId = id }; RestoreWorkspace(tab); Refresh(); Responsive(); SavePreferences();
    }
    private void AddWorkspace(bool noteScope)
    {
        Guid? noteId = noteScope ? selectedId : null; if (noteScope && noteId is null) { SetStatus("Select a note before opening a note tab."); return; }
        var existing = layoutState.Workspaces.FirstOrDefault(t => t.NoteId == noteId && (noteId is not null || t.NotebookId == notebookId));
        if (existing is not null) { ActivateWorkspace(existing.Id); return; }
        if (layoutState.Workspaces.Count >= 12) { SetStatus("Workspace limit: 12 tabs. Close a tab before adding another."); return; }
        if (!MayLeave()) return;
        CaptureWorkspace(); var id = Guid.NewGuid(); var book = noteId is Guid n ? app.State.Notes.First(x => x.Id == n).NotebookId : notebookId;
        var context = new WorkspaceContext(id, book, noteId, savedOnly, search.Text[..Math.Min(500, search.Text.Length)], sort, selectedId);
        layoutState = layoutState with { Workspaces = layoutState.Workspaces.Append(context).ToArray(), ActiveWorkspaceId = id }; SavePreferences(); RenderWorkspaceBar();
    }
    private void CloseWorkspace(Guid id)
    {
        if (!MayLeave()) return;
        CaptureWorkspace(); var remaining = layoutState.Workspaces.Where(t => t.Id != id).ToArray(); bool active = layoutState.ActiveWorkspaceId == id;
        layoutState = layoutState with { Workspaces = remaining, ActiveWorkspaceId = active ? null : layoutState.ActiveWorkspaceId };
        if (active) ClearReply();
        if (active && remaining.Length > 0) ActivateWorkspace(remaining[0].Id, alreadyConfirmed: true); else { RenderWorkspaceBar(); SavePreferences(); }
    }
    private void RenderWorkspaceBar()
    {
        workspaceBar.Children.Clear();
        if (layoutState.Workspaces.Count == 0) { var current = new Border { Child = Label(app.State.Notes.FirstOrDefault(n => n.Id == selectedId)?.Title ?? heading.Text, 13, true), BorderThickness = new Thickness(0, 0, 0, 3), Padding = new Thickness(12, 5, 12, 3), MinHeight = 34 }; current.SetResourceReference(Border.BorderBrushProperty, "Accent"); current.SetResourceReference(Border.BackgroundProperty, "Surface"); workspaceBar.Children.Add(current); }
        foreach (var tab in layoutState.Workspaces) {
            string name = tab.NoteId is Guid n ? app.State.Notes.FirstOrDefault(x => x.Id == n)?.Title ?? "Deleted note" : tab.NotebookId is Guid b ? "n/" + app.State.Notebooks.FirstOrDefault(x => x.Id == b)?.Name : "All notes";
            var row = new StackPanel { Orientation = Orientation.Horizontal }; var activate = Btn(name.Length > 22 ? name[..22] + "…" : name, () => ActivateWorkspace(tab.Id));
            activate.ToolTip = name; activate.MaxWidth = 250; activate.BorderThickness = new Thickness(0, 0, 0, 3); if (tab.Id == layoutState.ActiveWorkspaceId) activate.SetResourceReference(Control.BorderBrushProperty, "Accent"); row.Children.Add(activate); var close = Btn("×", () => CloseWorkspace(tab.Id)); AutomationProperties.SetName(close, "Close workspace " + name); row.Children.Add(close); workspaceBar.Children.Add(row);
        }
        var add = Btn("+", () => { }); AutomationProperties.SetName(add, "Add workspace tab"); var menu = new ContextMenu();
        foreach (bool noteScope in new[] { false, true }) { var item = new MenuItem { Header = noteScope ? "Add note tab" : "Add notebook tab" }; item.Click += (_, _) => { if (!vaultLocked) AddWorkspace(noteScope); }; menu.Items.Add(item); }
        add.Click += (_, _) => { if (!vaultLocked) { menu.PlacementTarget = add; menu.IsOpen = true; } }; workspaceBar.Children.Add(add);
    }
}
