using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Threadbook.Core;

namespace Threadbook.Desktop;

internal abstract class EditorWindow : Window
{
    protected readonly StackPanel Fields = new() { Margin = new Thickness(24) };
    protected readonly TextBlock Error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
    protected bool Dirty;
    protected readonly DockPanel EditorRoot = new();
    protected EditorWindow(Window owner, string title)
    {
        ApplicationBrand.Apply(this);
        Owner = owner; Title = title; Width = 600; Height = 650; MinWidth = 420; MinHeight = 350; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bg"); SetResourceReference(ForegroundProperty, "Text");
        Content = EditorRoot; EditorRoot.Children.Add(new ScrollViewer { Content = Fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        WindowChromeHelper.Attach(this);
        WindowSizing.Attach(this, this is NoteEditor ? 800 : 420, this is NoteEditor ? 720 : 350);
        Fields.Children.Add(new TextBlock { Text = title, FontSize = 25, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        Closing += (_, e) => { if (!MayClose()) e.Cancel = true; };
    }
    protected virtual bool MayClose() => !Dirty || DialogResult == true || MessageBox.Show(this, "Discard your unsaved changes?", "Unsaved draft", MessageBoxButton.YesNo) == MessageBoxResult.Yes;
    protected TextBox Input(string label, string value, int maxLength, bool multiline = false)
    {
        var box = new TextBox { Text = value, MaxLength = maxLength, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap, MinHeight = multiline ? 160 : 40, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden };
        AutomationProperties.SetName(box, label); var caption = new Label { Content = label, Target = box, Padding = new Thickness(0, 10, 0, 5) }; caption.SetResourceReference(Control.ForegroundProperty, "Text"); Fields.Children.Add(caption); Fields.Children.Add(box); box.TextChanged += (_, _) => Dirty = true; return box;
    }
    protected void Actions(Action save)
    {
        Fields.Children.Add(Error); var row = new StackPanel { Orientation = Orientation.Horizontal };
        var ok = new Button { Content = "Save", IsDefault = true }; ok.Click += (_, _) => save(); var cancel = new Button { Content = "Cancel", IsCancel = true }; row.Children.Add(ok); row.Children.Add(cancel); Fields.Children.Add(row);
    }
    protected void Accept() { Dirty = false; if (IsVisible) DialogResult = true; }
    protected void PinActions()
    {
        var actions = (StackPanel)Fields.Children[^1]; Fields.Children.Remove(actions); Fields.Children.Remove(Error);
        var footer = new StackPanel { Margin = new Thickness(24, 5, 24, 16) }; footer.Children.Add(Error); footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); EditorRoot.Children.Insert(0, footer);
    }
}
internal sealed class TextEditor : EditorWindow
{
    public TextEditor(Window owner, string title, string label, string initial, int maxLength, Func<string, bool> commit) : base(owner, title)
    {
        Height = 320; var box = Input(label, initial, maxLength); Actions(() => { if (commit(box.Text)) Accept(); else Error.Text = "Could not save. Check the name is unique and 1–60 characters. Your draft is preserved."; }); Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
    }
}
