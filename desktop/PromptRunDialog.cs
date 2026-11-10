using System.Windows;
using System.Windows.Controls;
using Threadbook.Core;

namespace Threadbook.Desktop;

/// <summary>Fills a prompt's {{variables}} and copies the finished prompt. Nothing is saved or sent.</summary>
internal sealed class PromptRunDialog : EditorWindow
{
    private readonly string source;
    private readonly IReadOnlyList<PromptVariable> variables;
    private readonly Dictionary<string, TextBox> inputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBox preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button copy = new() { Content = "Copy final prompt", IsDefault = true };

    public PromptRunDialog(Window owner, string title, string body) : base(owner, "Run prompt")
    {
        source = body;
        Width = 720; Height = 780;
        Fields.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(title) ? "Untitled prompt" : title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        variables = PromptTemplates.Parse(body);
        if (variables.Count == 0)
        {
            Fields.Children.Add(new TextBlock { Text = "This prompt has no {{variables}}. You can copy it as-is below.", TextWrapping = TextWrapping.Wrap });
        }
        foreach (var variable in variables)
        {
            var box = Input(variable.DefaultValue is null ? variable.Name : $"{variable.Name} (default: {variable.DefaultValue})", variable.DefaultValue ?? "", 10000, variable.DefaultValue is { Length: > 60 });
            inputs[variable.Name] = box;
            box.TextChanged += (_, _) => Refresh();
        }
        var previewCaption = new Label { Content = "Final prompt preview", Target = preview, Padding = new Thickness(0, 10, 0, 5) };
        previewCaption.SetResourceReference(Control.ForegroundProperty, "Text");
        System.Windows.Automation.AutomationProperties.SetName(preview, "Final prompt preview");
        Fields.Children.Add(previewCaption); Fields.Children.Add(preview);
        Fields.Children.Add(Error);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        copy.Click += (_, _) => Copy();
        var close = new Button { Content = "Close", IsCancel = true };
        row.Children.Add(copy); row.Children.Add(close);
        Fields.Children.Add(row);
        Refresh();
        Loaded += (_, _) => { if (variables.Count > 0) { var first = inputs[variables[0].Name]; first.Focus(); first.SelectAll(); } else copy.Focus(); };
    }

    protected override bool MayClose() => true;

    private PromptRender? Current()
    {
        try { return PromptTemplates.Render(source, inputs.ToDictionary(pair => pair.Key, pair => pair.Value.Text, StringComparer.OrdinalIgnoreCase)); }
        catch (DataException ex) { Error.Text = ex.Message; copy.IsEnabled = false; return null; }
    }

    private void Refresh()
    {
        var render = Current(); if (render is null) return;
        preview.Text = render.Text;
        copy.IsEnabled = render.Complete;
        Error.Text = render.Complete ? "Ready. Copying leaves this note unchanged." : "Still needed: " + string.Join(", ", render.Missing);
    }

    private void Copy()
    {
        var render = Current(); if (render is null || !render.Complete) { Refresh(); return; }
        try { Clipboard.SetText(render.Text); Error.Text = "Copied. Paste it into any AI chat."; }
        catch (System.Runtime.InteropServices.ExternalException) { Error.Text = "The clipboard is busy. Try Copy again."; }
    }
}
