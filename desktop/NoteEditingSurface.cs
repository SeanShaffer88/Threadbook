using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Threadbook.Core;
namespace Threadbook.Desktop;

internal sealed class NoteEditingSurface : UserControl, IDisposable
{
    internal readonly TextBox title, body, flair, find = new(), replacement = new();
    internal readonly ComboBox notebook;
    private readonly CheckBox matchCase = new() { Content = "Match case" };
    private readonly TextBlock counts = new(), matches = new();
    private readonly StackPanel outline = new(), findPanel = new();
    private readonly RichTextBox preview = new() { IsDocumentEnabled = true, IsReadOnly = true, BorderThickness = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel panel = new();
    internal readonly TextBlock Error = new() { TextWrapping = TextWrapping.Wrap };
    private bool disposed;
    public NoteEditFields Fields => new((Guid)(notebook.SelectedValue ?? Guid.Empty), title.Text, body.Text, flair.Text);
    public int CaretIndex => body.CaretIndex;
    public event EventHandler? FieldsChanged, SaveRequested, CancelRequested, NewRequested, ExportRequested, InsertNoteLinkRequested;
    internal Func<(Action<WikiLinkToken> Activate, Func<WikiLinkToken, string?> State)>? WikiPreviewContext;
    private long previewGeneration;

    public NoteEditingSurface(IReadOnlyList<Notebook> books, NoteEditFields initial)
    {
        MinWidth = 0; HorizontalAlignment = HorizontalAlignment.Stretch;
        Content = panel;
        notebook = new ComboBox { ItemsSource = books, SelectedValuePath = "Id", SelectedValue = initial.NotebookId, MinHeight = 36 };
        if (notebook.SelectedItem is null) notebook.SelectedIndex = 0;
        AutomationProperties.SetName(notebook, "Notebook"); panel.Children.Add(notebook);
        var comboText = new FrameworkElementFactory(typeof(TextBlock)); comboText.SetResourceReference(TextBlock.ForegroundProperty, "Text"); comboText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name")); notebook.ItemTemplate = new DataTemplate { VisualTree = comboText };
        title = Input("Title", initial.Title, 200);
        flair = Input("Flair (optional)", initial.Flair, 40);
        var toolbar = new WrapPanel { Margin = new Thickness(0, 10, 0, 8) };
        void Tool(string name, Action action) { var b = Button(name, action); toolbar.Children.Add(b); }
        Tool("Bold", () => Wrap("**", "**")); Tool("Italic", () => Wrap("*", "*")); Tool("Strike", () => Wrap("~~", "~~"));
        Tool("Link", () => Wrap("[", "](url)")); Tool("Code", () => Wrap("`", "`")); Tool("Heading", () => Wrap("## ", ""));
        Tool("List", () => Wrap("- ", "")); Tool("Task", () => Wrap("- [ ] ", "")); Tool("Quote", () => Wrap("> ", ""));
        Tool("Fence", () => Wrap("```\n", "\n```")); Tool("Rule", () => Wrap("\n---\n", "")); Tool("Table", () => Wrap("\n| Heading | Heading |\n| --- | --- |\n| ", " | Cell |\n"));
        Tool("Insert note link", () => InsertNoteLinkRequested?.Invoke(this, EventArgs.Empty));
        Tool("Source", ShowSourceAndFocus);
        Tool("Preview", ShowPreview); Tool("Find / Replace", () => { findPanel.Visibility = Visibility.Visible; find.Focus(); });
        Tool("Export draft…", () => ExportRequested?.Invoke(this, EventArgs.Empty)); panel.Children.Add(toolbar);
        body = Input("Note · Markdown source", initial.Body, 100000, true); body.Height = 285; body.AcceptsTab = true;
        preview.Height = 285; preview.Visibility = Visibility.Collapsed; panel.Children.Add(preview);
        find.MaxLength = 100000; replacement.MaxLength = 100000;
        AutomationProperties.SetName(find, "Find literal text"); AutomationProperties.SetName(replacement, "Replace with");
        findPanel.Children.Add(new TextBlock { Text = "Find literal text / Replace with" }); findPanel.Children.Add(find); findPanel.Children.Add(replacement);
        var controls = new WrapPanel(); controls.Children.Add(matchCase); controls.Children.Add(Button("Previous", () => Navigate(true))); controls.Children.Add(Button("Next", () => Navigate(false)));
        var replaceOne = Button("Replace current", () => Replace(false)); var replaceAll = Button("Replace all", () => Replace(true)); replaceOne.IsEnabled = replaceAll.IsEnabled = false; find.TextChanged += (_, _) => replaceOne.IsEnabled = replaceAll.IsEnabled = find.Text.Length > 0;
        controls.Children.Add(replaceOne); controls.Children.Add(replaceAll); controls.Children.Add(Button("Close find", () => { findPanel.Visibility = Visibility.Collapsed; body.Focus(); }));
        findPanel.Children.Add(controls); findPanel.Children.Add(matches); findPanel.Visibility = Visibility.Collapsed; panel.Children.Add(findPanel);
        panel.Children.Add(counts); panel.Children.Add(new Expander { Header="Outline · jump to source", Content=outline, IsExpanded=true, Margin=new Thickness(0,8,0,4) });

        panel.Children.Add(Error);
        title.TextChanged += Changed; flair.TextChanged += Changed; body.TextChanged += Changed;
        notebook.SelectionChanged += NotebookChanged;
        find.TextChanged += (_, _) => UpdateMatches(); matchCase.Checked += (_, _) => UpdateMatches(); matchCase.Unchecked += (_, _) => UpdateMatches();
        UpdateWriting();
    }
    private void Changed(object sender, TextChangedEventArgs e) { if (!disposed) { UpdateWriting(); FieldsChanged?.Invoke(this, EventArgs.Empty); } }
    private void NotebookChanged(object sender, SelectionChangedEventArgs e) { if (!disposed) FieldsChanged?.Invoke(this, EventArgs.Empty); }
    private TextBox Input(string label, string value, int maxLength, bool multiline = false)
    {
        var box = new TextBox { Text = value, MaxLength = maxLength, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap, MinWidth = 0, MinHeight = multiline ? 160 : 40, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden };
        AutomationProperties.SetName(box, label); var caption = new Label { Content = label, Target = box, Padding = new Thickness(0, 10, 0, 5) }; caption.SetResourceReference(Control.ForegroundProperty, "Text"); panel.Children.Add(caption); panel.Children.Add(box); return box;
    }
    public void FocusSource() => ShowSourceAndFocus();
    public void SetInteractionEnabled(bool enabled) => IsEnabled = enabled;
    public void Dispose() { if (disposed) return; disposed = true; title.TextChanged -= Changed; flair.TextChanged -= Changed; body.TextChanged -= Changed; notebook.SelectionChanged -= NotebookChanged; FieldsChanged = SaveRequested = CancelRequested = NewRequested = ExportRequested = InsertNoteLinkRequested = null; previewGeneration++; WikiPreviewContext = null; }
    internal bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (disposed || !IsEnabled) return false;
        if (key == Key.Escape && modifiers == ModifierKeys.None) { CancelRequested?.Invoke(this, EventArgs.Empty); return true; }
        if (modifiers != ModifierKeys.Control) return false; if((title.IsKeyboardFocused || flair.IsKeyboardFocused || find.IsKeyboardFocused || replacement.IsKeyboardFocused) && key is Key.B or Key.I or Key.K or Key.Z or Key.Y) return false; switch(key) {
            case Key.B: Wrap("**", "**"); break; case Key.I: Wrap("*", "*"); break; case Key.K: Wrap("[", "](url)"); break;
            case Key.F: findPanel.Visibility = Visibility.Visible; find.Focus(); break; case Key.S: SaveRequested?.Invoke(this, EventArgs.Empty); break;
            case Key.N: NewRequested?.Invoke(this, EventArgs.Empty); break;
            case Key.Z: ShowSourceAndFocus(); if(body.CanUndo) body.Undo(); break; case Key.Y: ShowSourceAndFocus(); if(body.CanRedo) body.Redo(); break; default: return false; } return true;
    }

    private Button Button(string text, Action action) { var b = new Button { Content = text, MinHeight = 30, Padding = new Thickness(8, 3, 8, 3) }; AutomationProperties.SetName(b, text); b.Click += (_, _) => { if (!disposed && IsEnabled) action(); }; return b; }
    private void ShowSourceAndFocus() { preview.Visibility = Visibility.Collapsed; body.Visibility = Visibility.Visible; body.Focus(); }
    private void Wrap(string before, string after)
    {
        if (body.Text.Length + before.Length + after.Length > 100000) { Error.Text = "Formatting would exceed the 100,000 character limit."; return; }
        ShowSourceAndFocus();
        int start = body.SelectionStart, length = body.SelectionLength; string selected = body.SelectedText;
        body.BeginChange(); try { body.SelectedText = before + selected + after; body.Select(start + before.Length, length); } finally { body.EndChange(); }
        body.Focus();
    }
    internal bool InsertWikiLink(Guid id, string title, int start, int length)
    {
        if (disposed || !IsEnabled || start < 0 || length < 0 || start + length > body.Text.Length) return false;
        string text = WikiLinks.FormatStable(id, title);
        if (body.Text.Length - length + text.Length > 100000) { Error.Text = "Note link would exceed the character limit."; return false; }
        ShowSourceAndFocus();
        body.BeginChange(); try { body.Select(start, length); body.SelectedText = text; body.Select(start + text.Length, 0); } finally { body.EndChange(); }
        return true;
    }
    private void UpdateWriting()
    {
        var parsed = Markdown.Parse(body.Text); counts.Text = $"{parsed.SourceWordCount:N0} source word count · {body.Text.Length:N0} / 100,000 characters";
        outline.Children.Clear(); foreach (var h in parsed.Headings.Take(100)) { var b = Button(new string(' ', (h.Level-1)*2) + h.Text, () => { ShowSourceAndFocus(); body.Select(h.SourceStart, h.SourceLength); body.ScrollToLine(body.GetLineIndexFromCharacterIndex(h.SourceStart)); }); b.HorizontalAlignment = HorizontalAlignment.Left; outline.Children.Add(b); }
        if(parsed.Headings.Count > 100) outline.Children.Add(new TextBlock { Text = "Outline shows first 100 headings." });
        if(preview.Visibility == Visibility.Visible) preview.Document = RenderPreview();
        UpdateMatches();
    }
    private System.Windows.Documents.FlowDocument RenderPreview()
    {
        long generation = ++previewGeneration;
        var context = WikiPreviewContext?.Invoke();
        return MarkdownView.Document(body.Text,
            onWikiLink: context is { } callbacks ? token => {
                if (!disposed && IsEnabled && generation == previewGeneration) callbacks.Activate(token);
            } : null,
            wikiState: context is { } states ? token => generation == previewGeneration ? states.State(token) : null : null);
    }
    private void ShowPreview() { preview.Document = RenderPreview(); body.Visibility = Visibility.Collapsed; preview.Visibility = Visibility.Visible; }
    private void UpdateMatches() { if(body is null) return; var list = TextEditing.Find(body.Text, find.Text, matchCase.IsChecked == true); int current = list.ToList().FindIndex(x => x.Start == body.SelectionStart && x.Length == body.SelectionLength); matches.Text = $"{(current < 0 ? 0 : current+1)} / {list.Count} matches · literal search"; }
    private void Navigate(bool previous) { var match = TextEditing.Navigate(body.Text, find.Text, previous ? body.SelectionStart-1 : body.SelectionStart+body.SelectionLength, previous, matchCase.IsChecked == true); if(match is not null) { ShowSourceAndFocus(); body.Select(match.Start, match.Length); body.ScrollToLine(body.GetLineIndexFromCharacterIndex(match.Start)); } UpdateMatches(); }
    private void Replace(bool all)
    {
        ShowSourceAndFocus();
        try { var edit = all ? TextEditing.ReplaceAll(body.Text, find.Text, replacement.Text, matchCase.IsChecked == true) : TextEditing.ReplaceCurrent(body.Text, find.Text, replacement.Text, body.SelectionStart, body.SelectionLength, matchCase.IsChecked == true);
            if(edit.ReplacementCount > 0) { body.BeginChange(); try { body.SelectAll(); body.SelectedText = edit.Text; body.Select(edit.SelectionStart, edit.SelectionLength); } finally { body.EndChange(); } }
            body.Focus(); UpdateMatches(); Error.Text = $"{edit.ReplacementCount} replacements. Explicit Save commits changes.";
        } catch(DataException ex) { Error.Text = ex.Message; }
    }

    public void WritingSmoke(string screenshot)
    {
        IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject { for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++) { var child=System.Windows.Media.VisualTreeHelper.GetChild(root,i); if(child is T match) yield return match; foreach(var nested in Visuals<T>(child)) yield return nested; } }
        UpdateLayout();
        void NotebookCaption(DependencyObject root, string name)
        {
            var label = Visuals<TextBlock>(root).SingleOrDefault(t => t.IsVisible && t.Text == name)
                ?? throw new Exception($"Missing visible notebook caption: {name} ({Theme.Mode})");
            System.Windows.Media.Brush? background = null;
            for (DependencyObject? parent = label; parent is not null; parent = System.Windows.Media.VisualTreeHelper.GetParent(parent))
                if (parent is Border border && border.Background is System.Windows.Media.SolidColorBrush fill && fill.Color.A == 255) { background = fill; break; }
            static double Luminance(System.Windows.Media.Color color) { static double Linear(byte value) { double n = value / 255.0; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); } return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B); }
            if (label.Foreground is not System.Windows.Media.SolidColorBrush foreground || foreground.Color.A != 255 || background is not System.Windows.Media.SolidColorBrush surface)
                throw new Exception($"Notebook caption has no opaque rendered foreground/background: {name}");
            double fg = Luminance(foreground.Color), bg = Luminance(surface.Color);
            if ((Math.Max(fg, bg) + .05) / (Math.Min(fg, bg) + .05) < 4.5) throw new Exception($"Notebook caption contrast below 4.5: {name} ({Theme.Mode})");
            if (label.ActualWidth <= 0 || label.ActualHeight <= 0 || label.DesiredSize.Width > label.ActualWidth + 1 || label.DesiredSize.Height > label.ActualHeight + 1)
                throw new Exception($"Notebook caption is clipped: {name} ({Theme.Mode})");
        }
        string previousTheme = Theme.Mode; object selectedBook = notebook.SelectedItem, selectedId = notebook.SelectedValue;
        var booksBefore = notebook.Items.Cast<Notebook>().Select(book => (book.Id, book.Name)).ToArray();
        try
        {
            foreach (string mode in new[] { "Dark", "Light", "System" })
            {
                Theme.Apply(mode); UpdateLayout(); NotebookCaption(notebook, ((Notebook)selectedBook).Name);
                notebook.IsDropDownOpen = true; notebook.UpdateLayout();
                foreach (Notebook item in notebook.Items) { var container = (ComboBoxItem)notebook.ItemContainerGenerator.ContainerFromItem(item); container.ApplyTemplate(); container.UpdateLayout(); NotebookCaption(container, item.Name); }
                notebook.IsDropDownOpen = false;
                if (!ReferenceEquals(notebook.SelectedItem, selectedBook) || !Equals(notebook.SelectedValue, selectedId) || !booksBefore.SequenceEqual(notebook.Items.Cast<Notebook>().Select(book => (book.Id, book.Name))))
                    throw new Exception($"Notebook identity/selection changed under theme {mode}");
            }
        }
        finally { notebook.IsDropDownOpen = false; Theme.Apply(previousTheme); }
        void SourceFocused() { UpdateLayout(); if(body.Visibility != Visibility.Visible || preview.Visibility != Visibility.Collapsed || !body.IsKeyboardFocused) throw new Exception("Source must be visible and focused"); }
        void Click(string name) => ((WrapPanel)panel.Children.OfType<WrapPanel>().First()).Children.OfType<Button>().Single(b => (string)b.Content == name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        body.Text = "# Heading\nalpha alpha\n**bold** and *italic*\n- [x] task\n"; body.Select(10,5); ShowPreview(); Click("Bold"); SourceFocused(); if(!body.Text.Contains("**alpha**")) throw new Exception("Format selection"); ShowPreview(); HandleShortcut(Key.Z,ModifierKeys.Control); SourceFocused(); if(body.Text.Contains("**alpha**")) throw new Exception("Single undo unit");
        find.Text="alpha"; replacement.Text="beta"; ShowPreview(); Navigate(false); SourceFocused(); ShowPreview(); Replace(false); SourceFocused(); ShowPreview(); Replace(true); SourceFocused(); if(body.Text.Contains("alpha")) throw new Exception("Replace");
        UpdateWriting(); ShowPreview(); if(preview.Document.Blocks.Count < 3 || outline.Children.Count != 1) throw new Exception("Preview/outline");
        body.Focus(); body.Select(0,0); HandleShortcut(Key.B,ModifierKeys.Control); if(!body.Text.StartsWith("****"))throw new Exception("Ctrl+B"); HandleShortcut(Key.Z,ModifierKeys.Control); if(body.Text.StartsWith("****"))throw new Exception("Ctrl+Z"); HandleShortcut(Key.Y,ModifierKeys.Control); if(!body.Text.StartsWith("****"))throw new Exception("Ctrl+Y"); HandleShortcut(Key.Z,ModifierKeys.Control);
        HandleShortcut(Key.F,ModifierKeys.Control); if(!find.IsKeyboardFocused)throw new Exception("Ctrl+F focus");
        findPanel.Visibility=Visibility.Collapsed; ShowPreview(); UpdateLayout(); MainWindow.CaptureVisual(Window.GetWindow(this), screenshot);
    }
}
