using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Threadbook.Core;
namespace Threadbook.Desktop;
public sealed partial class MainWindow
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    public async System.Threading.Tasks.Task RunLayoutSmokeTest()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
                var child = VisualTreeHelper.GetChild(parent, i); if (child is T match) yield return match;
                foreach (var nested in Visuals<T>(child)) yield return nested;
            }
        }
        void Visible(FrameworkElement element, string label) {
            Check(element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0, label + " visible");
            var bounds = element.TransformToAncestor((Visual)Content).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            var area = (FrameworkElement)Content;
            Check(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= area.ActualWidth + 1 && bounds.Bottom <= area.ActualHeight + 1, label + " within viewport: " + bounds);
        }
        void Critical() {
            Visible(layoutPicker, "Layout selector"); Visible(search, "Search");
            foreach (string name in new[] { "+ New note", "Search", "Search filters" })
                Visible(Visuals<Button>(this).First(b => AutomationProperties.GetName(b) == name), name);
            Check(uiCommands.ContainsKey("Library") && uiCommands.ContainsKey("Templates"), "overflow commands remain registered and menu accessible");
            if (detailPanel.IsVisible) {
                Visible(replyBox, "Reply composer");
                foreach (string name in new[] { "Save reply", "Cancel" }) Visible(Visuals<Button>(detailPanel).First(b => AutomationProperties.GetName(b) == name), name);
            }
        }
        Demo.Seed(app); notebookId = null; selectedId = app.State.Notes.First().Id; Refresh();
        var note = app.State.Notes.First(n => n.Id == selectedId);
        Check(Run(new PutReply(null, note.Id, null, "Layout root")), "Root fixture"); var root = app.State.Replies.Last();
        Check(Run(new PutReply(null, note.Id, root.Id, "Layout nested reply")), "Nested fixture"); var nestedReply = app.State.Replies.Last();
        var extra = Enumerable.Range(0, 260).Select(i => new Reply(Guid.NewGuid(), note.Id, root.Id, "Bounded graph reply " + i, root.Created.AddTicks(i + 1), root.Created.AddTicks(i + 1))).ToArray();
        Check(Run(new ReplaceAll(app.State with { Replies = app.State.Replies.Concat(extra).ToArray() })), "Large graph fixture");
        TargetReply(root, true); replyBox.Text = "Unsaved layout draft"; collapsed.Add(root.Id);
        var revision = app.State.Revision; var hash = SHA256.HashData(File.ReadAllBytes(store.PrimaryPath));
        var evidenceDir = Path.Combine(Path.GetDirectoryName(dataDir)!, "layout-evidence-" + Guid.NewGuid().ToString("N"));
        var screenshots = Path.Combine(evidenceDir, "layout-screenshots"); Directory.CreateDirectory(screenshots);
        var evidence = new List<string>();
        foreach (var layout in Enum.GetValues<NotebookLayout>()) {
            var priorLayout = layoutState.Layout; confirmLeaveForTest = () => false; ChangeLayout(layout);
            Check(layoutState.Layout == priorLayout && replyBox.Text == "Unsaved layout draft" && editingReplyId == root.Id, layout + " cancelled layout preserves draft");
            confirmLeaveForTest = () => true; ChangeLayout(layout); confirmLeaveForTest = null; TargetReply(root, true); replyBox.Text = "Unsaved layout draft"; RefreshFeed(); Responsive();
            if (layout == NotebookLayout.MindMapCentral) {
                var canvas = (Canvas)projection.Children.OfType<ScrollViewer>().Single().Content;
                Check(canvas.Children.OfType<Button>().Count() == 250, "Map initial content bound is 250");
                Check(Visuals<Button>(projection).Any(b => AutomationProperties.GetName(b).StartsWith("Load 250 more")), "Map exposes omitted count and load more");
            }
            Check(replyBox.Text == "Unsaved layout draft" && editingReplyId == root.Id && selectedId == note.Id && collapsed.Contains(root.Id), layout + " preserves draft and context");
            Width = 1480; Height = 940; await System.Threading.Tasks.Task.Delay(100); UpdateLayout(); Critical();
            Capture(Path.Combine(screenshots, layout + "-normal.png"));
            Width = MinWidth; Height = MinHeight; await System.Threading.Tasks.Task.Delay(100); UpdateLayout(); Critical();
            Capture(Path.Combine(screenshots, layout + "-minimum.png"));
            evidence.Add($"{layout}: normal/min screenshots; actual minimum {ActualWidth:F0}×{ActualHeight:F0}; critical controls within viewport; draft context retained.");
        }
        Check(layoutPicker.ItemTemplate is not null, "Explicit readable selector item template");
        foreach (var mode in new[] { "Light", "Dark", "System" }) {
            Theme.Apply(mode); await System.Threading.Tasks.Task.Delay(30); UpdateLayout();
            var foreground = ((SolidColorBrush)layoutPicker.Foreground).Color; var background = ((SolidColorBrush)layoutPicker.Background).Color;
            double L(Color c) { double C(byte v) { var x = v / 255.0; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); } return .2126 * C(c.R) + .7152 * C(c.G) + .0722 * C(c.B); }
            Check((Math.Max(L(foreground), L(background)) + .05) / (Math.Min(L(foreground), L(background)) + .05) >= 4.5, mode + " selector text contrast");
            var captionText = Visuals<TextBlock>(composerSaveButton).First(t => t.Text == "Save reply");
            var captionColor = ((SolidColorBrush)captionText.Foreground).Color; var accentColor = ((SolidColorBrush)composerSaveButton.Background).Color;
            Check((Math.Max(L(captionColor), L(accentColor)) + .05) / (Math.Min(L(captionColor), L(accentColor)) + .05) >= 4.5, mode + " rendered primary caption contrast");
        }
        Theme.Apply("Dark");
        UpdateLayout();
        var primaryCaption = Visuals<TextBlock>(composerSaveButton).First(t => t.Text == "Save reply");
        Check(((SolidColorBrush)primaryCaption.Foreground).Color == Colors.Black, "Rendered primary text inherits dark foreground");
        Check(!Visuals<ToolBar>(this).Any(), "Flat toolbar has no native grip or overflow chrome");
        Check(Visuals<System.Windows.Controls.Primitives.ScrollBar>(this).All(bar => { bar.ApplyTemplate(); return bar.Template.FindName("PART_Track", bar) is System.Windows.Controls.Primitives.Track; }), "All rendered scrollbars retain native track");
        Check(ReferenceEquals(System.Windows.Media.VisualTreeHelper.GetParent(workspaceHost), ((Border)detailPanel).Child), "Workspace host belongs to reader pane");
        Check(uiCommands.ContainsKey("Export") && uiCommands.ContainsKey("Import") && uiCommands.ContainsKey("Manage notebooks"), "canonical commands discoverable in menus");
        ClearReply(); ChangeLayout(NotebookLayout.KanbanFlow); MoveCard(note.Id, BoardLane.InProgress);
        Check(layoutState.Lanes[note.Id] == BoardLane.InProgress && app.State.Revision == revision && hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(store.PrimaryPath))), "Layout/board do not mutate canonical bytes or revision");
        var loaded = preferences.Load(app.State); Check(loaded.State.Layout == NotebookLayout.KanbanFlow && loaded.State.Lanes[note.Id] == BoardLane.InProgress, "Preference restart read");
        preferences.BeforeReplace = () => throw new IOException("Controlled preference failure"); SavePreferences(); Check(warningPanel.IsVisible && preferenceWarning.Text.Length > 0, "Preference failure visible");
        SetStatus("Canonical status does not clear preference warning"); Check(preferenceWarning.Text.Length > 0, "Warning independent of canonical status"); preferences.BeforeReplace = null; SavePreferences(); Check(warningPanel.Visibility == Visibility.Collapsed, "Preference retry");
        ClearReply(); ChangeLayout(NotebookLayout.MindMapCentral); collapsed.Add(root.Id); OpenMapReply(nestedReply);
        Check(selectedId == nestedReply.NoteId && !collapsed.Contains(root.Id) && replyElements.ContainsKey(nestedReply.Id), "Nested map opens canonical reply");
        ChangeLayout(NotebookLayout.WorkspaceTabs); notebookId = note.NotebookId; search.Text = ""; AddWorkspace(false); var first = layoutState.ActiveWorkspaceId!.Value;
        Width = 1480; Height = 940; UpdateLayout(); Capture(Path.Combine(screenshots, "WorkspaceTabs-active.png"));
        search.Text = "Small"; sort = NoteSort.Title; CaptureWorkspace(); AddWorkspace(true); var second = layoutState.ActiveWorkspaceId!.Value;
        Check(first != second, "Distinct notebook and note tabs"); search.Text = "different"; ActivateWorkspace(first);
        Check(search.Text == "Small" && sort == NoteSort.Title && selectedId == note.Id, "Tab restores search, sort, selection");
        TargetReply(root, true); replyBox.Text = "Workspace cancellation draft";
        var beforeClose = layoutState; var beforeTarget = replyTarget.Text; var beforeBar = workspaceBar.Children.Cast<UIElement>().ToArray();
        var beforeContext = (notebookId, savedOnly, sort, selectedId, showDetail, search.Text);
        int confirmations = 0;
        confirmLeaveForTest = () => { confirmations++; return false; };
        try {
            CloseWorkspace(first);
            Check(confirmations == 1 && layoutState == beforeClose && workspaceBar.Children.Cast<UIElement>().SequenceEqual(beforeBar) && (notebookId, savedOnly, sort, selectedId, showDetail, search.Text) == beforeContext && replyBox.Text == "Workspace cancellation draft" && editingReplyId == root.Id && parentReplyId == root.ParentId && replyTarget.Text == beforeTarget, "Cancelled active close preserves all context");
            confirmations = 0; confirmLeaveForTest = () => ++confirmations == 1;
            CloseWorkspace(first);
            Check(confirmations == 1 && layoutState.ActiveWorkspaceId == second && layoutState.Workspaces.Count == 1 && selectedId is null && search.Text == "different" && sort == NoteSort.Title && replyBox.Text.Length == 0 && editingReplyId is null && parentReplyId is null && replyTarget.Text == "Add a thought", $"Confirmed active close prompts once and activates remaining tab: count={confirmations}, active={layoutState.ActiveWorkspaceId}, expected={second}, tabs={layoutState.Workspaces.Count}, selected={selectedId}, expectedNote={note.Id}, search={search.Text}, sort={sort}, draft={replyBox.Text.Length}, edit={editingReplyId}, parent={parentReplyId}, target={replyTarget.Text}");
            AddWorkspace(false); var third = layoutState.ActiveWorkspaceId!.Value;
            TargetReply(root, true); replyBox.Text = "Nonactive close draft"; confirmations = 0;
            var inactiveState = layoutState; confirmLeaveForTest = () => { confirmations++; return false; };
            CloseWorkspace(second);
            Check(confirmations == 1 && layoutState == inactiveState && layoutState.ActiveWorkspaceId == third && replyBox.Text == "Nonactive close draft" && editingReplyId == root.Id, "Cancelled nonactive close preserves reply draft and workspace");
            confirmations = 0; confirmLeaveForTest = () => { confirmations++; return true; }; CloseWorkspace(second);
            Check(confirmations == 1 && layoutState.ActiveWorkspaceId == third && layoutState.Workspaces.All(t => t.Id != second) && replyBox.Text.Length == 0, "Confirmed nonactive close resolves draft once");
        } finally { confirmLeaveForTest = null; ClearReply(); }
        search.Clear();
        var sizingWindow = new Window { Width = 400, Height = 300, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
        WindowSizing.Attach(sizingWindow, 400, 300);
        try {
            sizingWindow.Show(); await System.Threading.Tasks.Task.Delay(100);
            Check(sizingWindow.Left > -10000 && sizingWindow.Top > -10000, "Initial placement recovered");
            double edge = sizingWindow.Left; sizingWindow.Left = edge - 80;
            await System.Threading.Tasks.Task.Delay(100);
            Check(Math.Abs(sizingWindow.Left - (edge - 80)) < 2, "Ordinary partial boundary movement is not snapped back");
            SendMessageW(new System.Windows.Interop.WindowInteropHelper(sizingWindow).Handle, 0x007E, 0, 0);
            await System.Threading.Tasks.Task.Delay(100);
            Check(sizingWindow.Left >= edge - 1, "Display change recovers out-of-bounds placement");
        } finally { sizingWindow.Close(); }
        Check(WindowSizing.Minimum(1180, 760, 800, 600) == new Size(800, 600), "Small work-area pure clamp");
        ClearReply(); MinWidth = 800; MinHeight = 600; Width = 800; Height = 600; ChangeLayout(NotebookLayout.Original); showDetail = false; Responsive();
        await System.Threading.Tasks.Task.Delay(100); UpdateLayout(); Critical(); Capture(Path.Combine(screenshots, "compact-800x600-list.png"));
        showDetail = true; Responsive(); UpdateLayout(); Critical(); Capture(Path.Combine(screenshots, "compact-800x600-detail.png"));
        MinWidth = 1180; MinHeight = 760; Width = 200; Height = 200; await System.Threading.Tasks.Task.Delay(100); UpdateLayout();
        Check(ActualWidth >= 1179 && ActualHeight >= 759, "Actual WPF minimum coerces smaller request");
        var editor = new NoteEditor(this, app.State.Notebooks, note, note.NotebookId, c => Run(c), recovery, app.State.Revision, dataDir);
        editor.Show(); await System.Threading.Tasks.Task.Delay(100); editor.Width = editor.MinWidth; editor.Height = editor.MinHeight; editor.UpdateLayout();
        foreach (string name in new[] { "Save", "Close · keep draft", "Discard draft" }) {
            var button = Visuals<Button>(editor).First(b => (string?)b.Content == name); var bounds = button.TransformToAncestor((Visual)editor.Content).TransformBounds(new Rect(0,0,button.ActualWidth,button.ActualHeight));
            Check(button.IsVisible && bounds.Bottom <= ((FrameworkElement)editor.Content).ActualHeight + 1, "Editor sticky " + name);
        }
        CaptureVisual(editor, Path.Combine(screenshots, "editor-minimum.png")); editor.Close();
        evidence.Add("Canonical SHA256 and revision unchanged by layout/board; preference restart and write-failure retry passed; nested map target and tab contexts passed; small work-area calculator + simulated 800×600 passed; actual resize coercion and editor sticky footer passed.");
        evidence.Add("Actual WPF regressions passed: cancelled active close leaves tab objects/bar/context/draft/target unchanged; confirmed close consumes only first decision of yes/no sequence and activates remaining context; nonactive close preserves draft. Owned auxiliary window initial recovery, ordinary partial boundary movement and display-change recovery passed.");
        evidence.Add("Small work area is simulated; physical multi-monitor/DPI movement and full assistive-technology testing remain unperformed.");
        File.WriteAllLines(Path.Combine(evidenceDir, "layout-evidence.txt"), evidence);
        Console.WriteLine("Layout evidence: " + evidenceDir);
        ClearReply();
    }
}
