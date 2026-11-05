using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Threadbook.Desktop;

public sealed partial class MainWindow
{
    private WindowFrame frame = null!;
    private readonly Dictionary<string, Action> uiCommands = new();
    private Button saveReplyCommand = null!;
    private Grid BuildCaption()
    {
        var caption = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        caption.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        caption.ColumnDefinitions.Add(new());
        caption.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var workspace = Label("Personal notebook", 14); workspace.VerticalAlignment = VerticalAlignment.Center;
        caption.Children.Add(workspace);
        search.Margin = new Thickness(24, 3, 24, 3); search.MinWidth = 140; search.Padding = new Thickness(10, 4, 10, 4);
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(search, true); search.ToolTip = "Search notes · Ctrl+F"; AutomationProperties.SetName(search, "Search your notebook");
                var searchHost = new Grid { MaxWidth = 468, HorizontalAlignment = HorizontalAlignment.Center }; searchHost.Children.Add(search);
        var searchHint = Label("Search notes…                         Ctrl+F", 13, false, "Muted"); searchHint.Margin = new Thickness(36, 0, 28, 0); searchHint.VerticalAlignment = VerticalAlignment.Center; searchHint.IsHitTestVisible = false; searchHint.TextWrapping = TextWrapping.NoWrap; searchHint.TextTrimming = TextTrimming.CharacterEllipsis; searchHost.Children.Add(searchHint);
        search.TextChanged += (_, _) => searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(searchHost, 1); caption.Children.Add(searchHost);
        var badge = Label("Private · Offline", 12, false, "Muted"); badge.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(badge, 2); caption.Children.Add(badge);
        void FitCaption() { caption.Width = Math.Max(200, ActualWidth - 310); searchHost.Width = Math.Min(468, Math.Max(220, ActualWidth - 550)); workspace.Visibility = ActualWidth < 1050 ? Visibility.Collapsed : Visibility.Visible; badge.Visibility = ActualWidth < 900 ? Visibility.Collapsed : Visibility.Visible; }
        SizeChanged += (_, _) => FitCaption(); Loaded += (_, _) => FitCaption();
        return caption;
    }
    private void InitializeCommands(DockPanel root)
    {
        commandRegistry = new(CommandPrivacy);
        var toolbar = new DockPanel { Margin = new Thickness(12, 4, 12, 4), LastChildFill = true, Height = 36 };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(left, Dock.Left); DockPanel.SetDock(right, Dock.Right); toolbar.Children.Add(left); toolbar.Children.Add(right);
        toolbar.SetResourceReference(Panel.BackgroundProperty, "Bg");
        var menu = new Menu(); menu.SetResourceReference(Control.BackgroundProperty, "Bg");
        MenuItem Group(string name) { var item = new MenuItem { Header = name }; AutomationProperties.SetName(item, name + " menu"); menu.Items.Add(item); return item; }
        MenuItem Command(MenuItem parent, string name, Action action) {
            string id = CommandId(name);
            commandRegistry.Add(new(id, name, "Actions", () => CommandAvailability(name), () => { action(); return true; }));
            uiCommands.Add(name, () => commandRegistry.TryExecute(id)); var item = new MenuItem { Header = name }; AutomationProperties.SetName(item, name);
            item.Click += (_, _) => uiCommands[name]();
            parent.SubmenuOpened += (_, _) => { item.IsEnabled = CommandAvailability(name) is null; item.ToolTip = CommandAvailability(name); };
            parent.Items.Add(item); return item;
        }
        var file = Group("File"); Command(file, "New note", NewNote); Command(file, "Export", Export); Command(file, "Import", Import);
        Command(file, "Import files…", () => ImportText(false)); Command(file, "Import folder…", () => ImportText(true));
        Command(file, "Encrypted copy…", CreatePrivateCopy); Command(file, "Attachments…", OpenAttachments); Command(file, "Backup…", BackupPrivateLibrary); Command(file, "Restore backup…", RestorePrivateLibrary);
        Command(file, "Open vault…", () => { var picker = new OpenFolderDialog { Title = "Select an existing Threadbook data directory" }; if (picker.ShowDialog(this) == true) OpenPrivateDirectory(picker.FolderName); });
        var edit = Group("Edit"); var saveMenu = Command(edit, "Save reply", SaveReply);
        commandRegistry.Add(new("action:undo", "Undo", "Actions", () => ApplicationCommands.Undo.CanExecute(null, replyBox) ? null : "Nothing to undo in the reply.", () => { ApplicationCommands.Undo.Execute(null, replyBox); return true; }));
        commandRegistry.Add(new("action:redo", "Redo", "Actions", () => ApplicationCommands.Redo.CanExecute(null, replyBox) ? null : "Nothing to redo in the reply.", () => { ApplicationCommands.Redo.Execute(null, replyBox); return true; }));
        edit.Items.Add(new MenuItem { Header = "Undo", Command = ApplicationCommands.Undo, CommandTarget = replyBox }); edit.Items.Add(new MenuItem { Header = "Redo", Command = ApplicationCommands.Redo, CommandTarget = replyBox });
        var view = Group("View"); Command(view, "Command palette", OpenCommandPalette); Command(view, "History", () => OpenArchive(false)); Command(view, "Trash", () => OpenArchive(true)); Command(view, "Search", () => { search.Focus(); search.SelectAll(); }); Command(view, "Search filters", OpenLibraryFilters);
        Command(view, "Theme: Dark / Light / System", CycleTheme); Command(view, "Due / overdue", () => { if (!MayLeave()) return; overdueOnly = !overdueOnly; RefreshFeed(); });
        var notebook = Group("Notebook"); Command(notebook, "Manage notebooks", OpenNotebookManagement); Command(notebook, "Library", OpenLibrary); Command(notebook, "Templates", OpenTemplates);
        if (!store.Files.IsEncrypted) Command(notebook, "Lock vault", () => TryLockVault());
        if (store.Files.IsEncrypted) {
            Command(notebook, "Lock vault", () => TryLockVault());
            foreach (var minutes in new[] { 0, 5, 15, 30 }) { int value = minutes; Command(notebook, "Auto-lock: " + (value == 0 ? "Off" : value + " minutes"), () => { idleMinutes = value; lastPrivateInput = System.Diagnostics.Stopwatch.GetTimestamp(); }); }
        }
        left.Children.Add(menu); left.Children.Add(new Border { Width = 1, Height = 24, Margin = new Thickness(7, 4, 7, 4), Background = (Brush)Application.Current.Resources["Line"] });
        saveReplyCommand = IconButton("Save reply", "M3,2 L15,2 18,5 18,18 2,18 2,2 Z M6,2 L6,8 14,8 14,2 M6,18 L6,12 14,12 14,18", () => uiCommands["Save reply"]());
        left.Children.Add(saveReplyCommand);
        left.Children.Add(new Button { Content = "↶", ToolTip = "Undo reply", Command = ApplicationCommands.Undo, CommandTarget = replyBox });
        left.Children.Add(new Button { Content = "↷", ToolTip = "Redo reply", Command = ApplicationCommands.Redo, CommandTarget = replyBox });
        left.Children.Add(new Border { Width = 1, Height = 24, Margin = new Thickness(7, 4, 7, 4), Background = (Brush)Application.Current.Resources["Line"] });
        left.Children.Add(IconButton("Search", "M14,14 L19,19 M16,9 A7,7 0 1 1 2,9 A7,7 0 1 1 16,9", () => uiCommands["Search"]()));
        left.Children.Add(IconButton("Search filters", "M2,3 L18,3 12,10 12,18 8,16 8,10 Z", () => uiCommands["Search filters"]()));
        left.Children.Add(new Border { Width = 1, Height = 24, Margin = new Thickness(7, 4, 7, 4), Background = (Brush)Application.Current.Resources["Line"] }); left.Children.Add(Label("Layout:", 12)); layoutPicker.MinWidth = 130; layoutPicker.Width = 150; left.Children.Add(layoutPicker);
        left.Children.Add(new Border { Width = 1, Height = 24, Margin = new Thickness(7, 4, 7, 4), Background = (Brush)Application.Current.Resources["Line"] }); var libraryButton = Btn("Library", () => uiCommands["Library"]()); var templatesButton = Btn("Templates", () => uiCommands["Templates"]()); right.Children.Add(libraryButton); right.Children.Add(templatesButton);
        var lockButton = IconButton("Lock vault", "M5,9 L15,9 15,18 5,18 Z M7,9 L7,5 A3,3 0 0 1 13,5 L13,9", () => { if (uiCommands.TryGetValue("Lock vault", out var action)) action(); }); lockButton.IsEnabled = store.Files.IsEncrypted; right.Children.Add(lockButton);
        themeButton = Btn("Theme: Dark", () => uiCommands["Theme: Dark / Light / System"]());
        var newNoteButton = Btn("+ New note", () => uiCommands["New note"](), true); right.Children.Add(newNoteButton);
        var overflow = Btn("···", () => { }); AutomationProperties.SetName(overflow, "More toolbar actions"); var overflowMenu = new ContextMenu();
        foreach (var name in new[] { "Search", "Search filters", "Library", "Templates", "Theme: Dark / Light / System" }) { var item = new MenuItem { Header = name }; item.Click += (_, _) => { if (!vaultLocked) uiCommands[name](); }; overflowMenu.Items.Add(item); }
        overflow.Click += (_, _) => { if (!vaultLocked) { overflowMenu.PlacementTarget = overflow; overflowMenu.IsOpen = true; } }; right.Children.Insert(0, overflow);
        var optional = left.Children.Cast<UIElement>().Skip(1).Where(x => x != layoutPicker && !(x is Button button && (AutomationProperties.GetName(button) == "Search" || AutomationProperties.GetName(button) == "Search filters"))).ToArray();
        void FitToolbar() { bool compact = ActualWidth < 1050; foreach (var element in optional) element.Visibility = compact ? Visibility.Collapsed : Visibility.Visible; libraryButton.Visibility = templatesButton.Visibility = ActualWidth < 900 ? Visibility.Collapsed : Visibility.Visible; }
        SizeChanged += (_, _) => FitToolbar(); FitToolbar();
        foreach (var button in left.Children.OfType<Button>().Concat(right.Children.OfType<Button>())) { button.FontSize = 13; button.Height = 34; button.Padding = new Thickness(8, 4, 8, 4); }
        toolbar.Children.Add(new Border());
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Insert(0, toolbar);
        saveMenu.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding("IsEnabled") { Source = saveReplyCommand });
        CommandManager.AddPreviewCanExecuteHandler(replyBox, (_, e) => { if (CommandPrivacy() is not null) { e.CanExecute = false; e.Handled = true; } });
        CommandManager.AddPreviewExecutedHandler(replyBox, (_, e) => { if (CommandPrivacy() is not null && (e.Command == ApplicationCommands.Undo || e.Command == ApplicationCommands.Redo)) e.Handled = true; });
        replyBox.TextChanged += (_, _) => UpdateReplyCommand(); UpdateReplyCommand();
    }
    private void UpdateReplyCommand()
    {
        bool available = CommandAvailability("Save reply") is null;
        if (saveReplyCommand is not null) saveReplyCommand.IsEnabled = available;
        if (composerSaveButton is not null) composerSaveButton.IsEnabled = available;
        CommandManager.InvalidateRequerySuggested();
    }
    private static Button IconButton(string name, string geometry, Action action)
    {
        var button = Btn(name, action); button.ToolTip = name; button.Padding = new Thickness(8, 5, 8, 5); button.BorderThickness = new Thickness(0);
        var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Width = 18, Height = 18, Stretch = Stretch.Uniform, StrokeThickness = 1.4 };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Text"); button.Content = path; return button;
    }
}
