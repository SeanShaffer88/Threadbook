using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Threadbook.Core;
namespace Threadbook.Desktop;

internal interface ILibraryDialogHost
{
    bool AllowLibraryDialogAction();
}

// Small, scrollable native forms; commands and validation remain in Core/Storage.
internal sealed class LibraryDialog : Window
{
    internal readonly StackPanel Fields = new() { Margin = new Thickness(20) };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap };
    internal LibraryDialog(Window owner, string title)
    {
        ApplicationBrand.Apply(this);
        Owner = owner; Title = title; Width = 600; Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bg"); SetResourceReference(ForegroundProperty, "Text");
        Content = new ScrollViewer { Content = Fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        WindowChromeHelper.Attach(this);
        WindowSizing.Attach(this, 380, 320);
        Fields.Children.Add(new TextBlock { Text = title, FontSize = 24, Margin = new Thickness(0,0,0,16) }); Fields.Children.Add(error);
    }
    internal TextBox Text(string caption, string value = "", int max = 200, bool multiline = false)
    {
        var box = new TextBox { Text = value, MaxLength = max, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap, MinHeight = multiline ? 180 : 36, MaxHeight = multiline ? 280 : 60, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var label = new Label { Content = caption, Target = box }; label.SetResourceReference(ForegroundProperty, "Text"); Fields.Children.Add(label); Fields.Children.Add(box);
        AutomationProperties.SetName(box, caption); return box;
    }
    internal ComboBox Choice(string caption, IEnumerable<(Guid? Id, string Name)> values, Guid? selected = null)
    {
        var box = new ComboBox { MinHeight = 36, DisplayMemberPath = "Name", SelectedValuePath = "Id" };
        box.SetResourceReference(ForegroundProperty, "Text");
        box.ItemsSource = values.Select(x => new ChoiceItem(x.Id, x.Name)).ToArray(); box.SelectedValue = selected;
        var label = new Label { Content = caption, Target = box }; label.SetResourceReference(ForegroundProperty, "Text"); Fields.Children.Add(label); Fields.Children.Add(box); AutomationProperties.SetName(box, caption); return box;
    }
    internal void Action(string text, Action action)
    {
        var button = new Button { Content = text, MinHeight = 36, Margin = new Thickness(0,8,0,0) };
        button.Click += (_, _) => { for (Window? w = Owner; w is not null; w = w.Owner) if (w is ILibraryDialogHost host && !host.AllowLibraryDialogAction()) return; try { action(); } catch (Exception ex) when (ex is DataException or System.IO.IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or ArgumentException) { error.Text = ex.Message; } };
        Fields.Children.Add(button);
    }
    internal void Save(Func<bool> action) { Action("Save", () => { if (action()) DialogResult = true; else error.Text = "Save failed. Review the main window status; your entries remain here."; }); }
    internal static DateTimeOffset? Utc(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!DateTimeOffset.TryParseExact(text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)) throw new FormatException("Use UTC date/time: yyyy-MM-dd HH:mm, or leave blank.");
        return value;
    }
    private sealed record ChoiceItem(Guid? Id, string Name);
}
