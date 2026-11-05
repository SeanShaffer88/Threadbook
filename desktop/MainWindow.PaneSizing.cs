using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Threadbook.Desktop;

public sealed partial class MainWindow
{
    private PaneSizePreferenceStore panePreferences = null!;
    private PaneSizeState paneState = PaneSizeState.Default;
    private readonly List<Thumb> paneHandles = new();
    private readonly TextBlock paneWarning = new() { TextWrapping = TextWrapping.Wrap };
    private DockPanel paneWarningHost = null!;
    private bool PaneWritable => ArchiveUnlocked && !ArchivePending;
    private void InitializePaneSizing(DockPanel root)
    {
        panePreferences = new(store, () => PaneWritable);
        var loaded = panePreferences.Load(); paneState = loaded.State; paneWarning.Text = loaded.Warning ?? "";
        var warning = new DockPanel(); paneWarningHost = warning; warning.Children.Add(paneWarning);
        warning.Visibility = string.IsNullOrEmpty(paneWarning.Text) ? Visibility.Collapsed : Visibility.Visible;
        var reset = Btn("Reset pane widths…", () => {
            if (!PaneWritable) return;
            if (MessageBox.Show(this, "Preserve the stored pane preferences and reset all pane widths? Layout preferences and notes are unchanged.", "Reset pane widths", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var result = panePreferences.Reset();
            if (result.Success) { paneState = PaneSizeState.Default; paneWarning.Text = ""; Responsive(); }
            else paneWarning.Text = result.Error!.Message;
            warning.Visibility = string.IsNullOrEmpty(paneWarning.Text) ? Visibility.Collapsed : Visibility.Visible;
        });
        DockPanel.SetDock(reset, Dock.Right); warning.Children.Insert(0, reset);
        var retry = Btn("Retry saving pane widths", () => {
            if (!PaneWritable) return;
            var result = panePreferences.Save(paneState);
            paneWarning.Text = result.Success ? "" : "Pane widths remain in memory: " + result.Error!.Message;
            warning.Visibility = result.Success ? Visibility.Collapsed : Visibility.Visible;
        });
        DockPanel.SetDock(retry, Dock.Right); warning.Children.Insert(0, retry);
        DockPanel.SetDock(warning, Dock.Top); root.Children.Insert(root.Children.Count - 1, warning);
        for (int column = 0; column < 2; column++) {
            int index = column;
            var handle = new Thumb { Width = 6, HorizontalAlignment = HorizontalAlignment.Right, Cursor = Cursors.SizeWE, Focusable = true, ToolTip = "Drag to resize; Left/Right resize by 16; Escape cancels drag" };
            var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var grip = new FrameworkElementFactory(typeof(Border)); grip.SetValue(FrameworkElement.WidthProperty, 2d); grip.SetValue(FrameworkElement.HeightProperty, 36d); grip.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); grip.SetResourceReference(Border.BackgroundProperty, "Muted"); border.AppendChild(grip);
            handle.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = border };
            AutomationProperties.SetName(handle, index == 0 ? "Resize notebooks pane" : "Resize notes and conversation panes");
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(handle, true);
            Grid.SetColumn(handle, index); Panel.SetZIndex(handle, 20); columns.Children.Add(handle); paneHandles.Add(handle);
            double start = 0, delta = 0; NotebookLayout dragLayout = default;
            handle.DragStarted += (_, _) => { start = columns.ColumnDefinitions[index].ActualWidth; delta = 0; dragLayout = layoutState.Layout; };
            handle.DragDelta += (_, e) => { if (!PaneWritable || narrow || dragLayout != layoutState.Layout) return; delta += e.HorizontalChange; ResizePane(index, start + delta, false); };
            handle.DragCompleted += (_, e) => {
                if (e.Canceled || !PaneWritable || narrow || dragLayout != layoutState.Layout) { Responsive(); return; }
                if (Math.Abs(delta) > .01) ResizePane(index, columns.ColumnDefinitions[index].ActualWidth, true);
            };
            handle.PreviewKeyDown += (_, e) => {
                if (e.Key == Key.Escape && handle.IsDragging) { handle.CancelDrag(); e.Handled = true; return; }
                if (e.Key is not (Key.Left or Key.Right) || !PaneWritable || narrow) return;
                ResizePane(index, columns.ColumnDefinitions[index].ActualWidth + (e.Key == Key.Left ? -16 : 16), true); e.Handled = true;
            };
        }
    }
    private double DesiredPane(string role, double fallback) => paneState.Layouts.TryGetValue(layoutState.Layout.ToString(), out var roles) ? roles.GetValueOrDefault(role, fallback) : fallback;
    private void ApplyPaneSizing()
    {
        if (paneHandles.Count == 0) return;
        bool original = layoutState.Layout == NotebookLayout.Original;
        paneHandles[0].Visibility = original && !narrow ? Visibility.Visible : Visibility.Collapsed;
        paneHandles[1].Visibility = !narrow ? Visibility.Visible : Visibility.Collapsed;
        if (narrow) return;
        double area = Math.Max(0, columns.ActualWidth > 0 ? columns.ActualWidth : ActualWidth);
        double sidebarWidth = original ? Math.Clamp(DesiredPane("Sidebar", 240), 200, Math.Max(200, area - 680)) : 0;
        double fallback = layoutState.Layout switch { NotebookLayout.FocusEditor => 285, NotebookLayout.WorkspaceTabs => 350, NotebookLayout.Original => 390, _ => Math.Max(260, area - 430) };
        double primary = Math.Clamp(DesiredPane(original ? "Feed" : "Primary", fallback), 260, Math.Max(260, area - sidebarWidth - 420));
        columns.ColumnDefinitions[0].Width = new GridLength(sidebarWidth);
        columns.ColumnDefinitions[1].Width = new GridLength(primary);
        columns.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
    }
    private void ResizePane(int index, double requested, bool commit)
    {
        if (!PaneWritable || narrow || !double.IsFinite(requested)) return;
        bool original = layoutState.Layout == NotebookLayout.Original;
        if (index == 0 && !original) return;
        double other = original ? columns.ColumnDefinitions[index == 0 ? 1 : 0].ActualWidth : 0;
        double width = Math.Clamp(requested, index == 0 ? 200 : 260, Math.Max(index == 0 ? 200 : 260, Math.Min(4096, columns.ActualWidth - other - 420)));
        columns.ColumnDefinitions[index].Width = new GridLength(width); columns.UpdateLayout();
        if (!commit) return;
        var layouts = paneState.Layouts.ToDictionary(x => x.Key, x => new Dictionary<string, double>(x.Value));
        string layout = layoutState.Layout.ToString(); if (!layouts.TryGetValue(layout, out var roles)) layouts[layout] = roles = new();
        roles[index == 0 ? "Sidebar" : original ? "Feed" : "Primary"] = width;
        paneState = new(1, layouts);
        var result = panePreferences.Save(paneState);
        paneWarning.Text = result.Success ? "" : "Pane widths remain in memory: " + result.Error!.Message;
        paneWarningHost.Visibility = result.Success ? Visibility.Collapsed : Visibility.Visible;
    }
}
