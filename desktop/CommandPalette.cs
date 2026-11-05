using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Threadbook.Desktop;

internal sealed class CommandPalette : Window
{
    internal readonly TextBox query = new() { MaxLength = 256, Margin = new Thickness(0, 8, 0, 12), Padding = new Thickness(12, 8, 12, 8) };
    internal readonly ListBox results = new() { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock reason = new() { Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly CommandRegistry registry;
    private readonly Func<IEnumerable<PaletteCommand>> entries;
    internal CommandPalette(Window owner, CommandRegistry registry, Func<IEnumerable<PaletteCommand>> entries)
    {
        this.registry = registry; this.entries = entries; Owner = owner;
        ApplicationBrand.Apply(this); Title = "Threadbook · Command palette"; Width = 650; Height = 510;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg"); SetResourceReference(ForegroundProperty, "Text");
        results.SetResourceReference(Control.BackgroundProperty, "Bg"); results.SetResourceReference(Control.ForegroundProperty, "Text");
        var rowStyle = new Style(typeof(ListBoxItem));
        rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, Application.Current.Resources["Text"]));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 8, 10, 8)); border.AppendChild(presenter);
        rowStyle.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border }));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true }; selected.Setters.Add(new Setter(Control.BackgroundProperty, Application.Current.Resources["Selected"])); rowStyle.Triggers.Add(selected);
        results.ItemContainerStyle = rowStyle;
        var panel = new DockPanel { Margin = new Thickness(20) };
        var title = new TextBlock { Text = "Commands & quick jump     Ctrl+Shift+P", FontSize = 17, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); DockPanel.SetDock(title, Dock.Top); panel.Children.Add(title);
        AutomationProperties.SetName(query, "Search commands, notebooks and notes"); DockPanel.SetDock(query, Dock.Top); panel.Children.Add(query);
        DockPanel.SetDock(reason, Dock.Bottom); panel.Children.Add(reason); panel.Children.Add(results); Content = panel;
        query.TextChanged += (_, _) => RefreshResults(); results.SelectionChanged += (_, _) => ShowReason();
        results.MouseDoubleClick += (_, _) => ExecuteSelection();
        PreviewKeyDown += (_, e) => {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter) { ExecuteSelection(); e.Handled = true; }
            else if (e.Key is Key.Up or Key.Down) { results.SelectedIndex = Math.Clamp(results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Math.Max(0, results.Items.Count - 1)); if (results.SelectedItem is not null) results.ScrollIntoView(results.SelectedItem); e.Handled = true; }
            else if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { FocusQuery(); e.Handled = true; }
        };
        Closed += (_, _) => { query.Clear(); results.Items.Clear(); reason.Text = ""; Title = "Threadbook"; };
        Loaded += (_, _) => FocusQuery(); RefreshResults();
    }
    internal void FocusQuery() { query.Focus(); query.SelectAll(); }
    private bool closing;
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { closing = true; base.OnClosing(e); }
    internal void RefreshResults()
    {
        if (closing) return;
        results.Items.Clear();
        foreach (var command in CommandRegistry.Search(entries(), query.Text)) {
            string? disabled = registry.DisabledReason(command);
            var label = new TextBlock { Text = command.Group + "  ·  " + command.Label, TextTrimming = TextTrimming.CharacterEllipsis };
            var item = new ListBoxItem { Content = label, Tag = command, Style = results.ItemContainerStyle, Opacity = disabled is null ? 1 : 0.55, ToolTip = disabled };
            results.Items.Add(item);
        }
        if (results.Items.Count > 0) results.SelectedIndex = 0;
        ShowReason();
    }
    private void ShowReason() => reason.Text = results.SelectedItem is ListBoxItem { Tag: PaletteCommand command } ? registry.DisabledReason(command) ?? "Enter to run · ↑ ↓ to select · Esc to close" : "No matching commands, notebooks or notes.";
    private void ExecuteSelection()
    {
        if (results.SelectedItem is not ListBoxItem { Tag: PaletteCommand command }) return;
        if (registry.DisabledReason(command) is string disabled) { reason.Text = disabled; return; }
        // Close before running an action that can open a modal dialog or lock its owner.
        // Keep failed/cancelled navigation open so the user can revise the selection.
        if (command.Group == "Actions") { Close(); registry.TryExecute(command); }
        else if (registry.TryExecute(command)) Close(); else ShowReason();
    }
}
