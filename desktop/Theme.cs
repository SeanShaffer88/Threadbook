using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Threadbook.Desktop;

internal static class Theme
{
    public static string Mode { get; private set; } = "Dark";
    public static void Apply(string mode)
    {
        Mode = mode;
        bool light = mode == "Light";
        if (mode == "System")
        {
            try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); light = key?.GetValue("AppsUseLightTheme") is int n && n != 0; } catch (System.Security.SecurityException) { light = false; }
        }
        var r = Application.Current.Resources;
        void Color(string name, string hex) => r[name] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        // TesselGUI revision 2 tokens (docs/tokens.json in SeanShaffer88/TesselGUI, v1.2.x):
        // dark is GitHub Dark; light keeps Tessel light surfaces with the GitHub Light accent family.
        // "Accent" carries the selection-indicator role; primary actions use "Action".
        Color("Bg", light ? "#EDEDF0" : "#0D1117"); Color("Surface", light ? "#FFFFFF" : "#0E1218");
        Color("Hover", light ? "#E4E4E9" : "#0F1319"); Color("Raised", light ? "#E4E4E9" : "#0F1319");
        Color("Text", light ? "#202023" : "#F0F6FC");
        Color("Muted", light ? "#50505B" : "#9198A1"); Color("Line", light ? "#B8B8BD" : "#3D444D");
        Color("Accent", light ? "#0969DA" : "#2F81F7"); Color("Selected", light ? "#DDDDE4" : "#1F2630");
        Color("Focus", light ? "#0550AE" : "#79B8FF"); Color("DeepAccent", light ? "#0550AE" : "#15263C");
        Color("AccentText", light ? "#F1F1F3" : "#F0F6FC");
        Color("Action", "#1A7F37"); Color("ActionHover", "#238636"); Color("ActionPressed", "#116329");
        Color("Grid", light ? "#F2F2F5" : "#1C2128");
        Color("Info", light ? "#174E85" : "#A5D6FF"); Color("Warning", light ? "#735100" : "#D29922");
        Color("Success", light ? "#236340" : "#3FB950"); Color("Danger", light ? "#9D2B1F" : "#FFA198");
        if (r.Contains(typeof(Button))) return;
        // Text inherits the host foreground, including white captions on green action buttons.
        r[typeof(TextBlock)] = new Style(typeof(TextBlock));
        var control = new Style(typeof(Control)); control.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI"))); control.Setters.Add(new Setter(Control.FontSizeProperty, 14.0)); control.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Text"))); r[typeof(Control)] = control;
        var button = new Style(typeof(Button), control);
        button.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Surface")));
        button.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Line")));
        button.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 7, 12, 7)));
        button.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0)));
        button.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand));
        var template = new ControlTemplate(typeof(Button)); var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3)); border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty)); border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty)); border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty)); content.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty)); content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(content); template.VisualTree = border; button.Setters.Add(new Setter(Control.TemplateProperty, template));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(UIElement.OpacityProperty, .82)); button.Triggers.Add(hover);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true }; focus.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Focus"))); focus.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(2))); button.Triggers.Add(focus); r[typeof(Button)] = button;
        foreach (var type in new[] { typeof(Menu), typeof(ContextMenu), typeof(MenuItem), typeof(ToolBar) }) {
            var style = new Style(type, control); style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Bg"))); r[type] = style;
        }
        var comboItem = new Style(typeof(ComboBoxItem), control); comboItem.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Surface")));
        var selected = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true }; selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Selected"))); comboItem.Triggers.Add(selected); r[typeof(ComboBoxItem)] = comboItem;
        var combo = new Style(typeof(ComboBox), control); combo.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Surface"))); combo.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Line")));
        var comboTemplate = new ControlTemplate(typeof(ComboBox));
        var comboRoot = new FrameworkElementFactory(typeof(Grid));
        var toggle = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.ToggleButton)); toggle.SetValue(Control.BackgroundProperty, new DynamicResourceExtension("Surface")); toggle.SetValue(Control.ForegroundProperty, new DynamicResourceExtension("Text"));
        var toggleTemplate = new ControlTemplate(typeof(System.Windows.Controls.Primitives.ToggleButton)); var toggleBorder = new FrameworkElementFactory(typeof(Border)); toggleBorder.SetResourceReference(Border.BackgroundProperty, "Surface"); toggleBorder.SetResourceReference(Border.BorderBrushProperty, "Line"); toggleBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1)); toggleBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        var toggleText = new FrameworkElementFactory(typeof(ContentPresenter)); toggleText.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 4, 8, 4)); toggleText.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty)); toggleText.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ContentControl.ContentTemplateProperty)); toggleText.SetValue(ContentPresenter.ContentStringFormatProperty, new TemplateBindingExtension(ContentControl.ContentStringFormatProperty)); toggleText.SetValue(ContentPresenter.ContentTemplateSelectorProperty, new TemplateBindingExtension(ContentControl.ContentTemplateSelectorProperty)); toggleBorder.AppendChild(toggleText); toggleTemplate.VisualTree = toggleBorder; toggle.SetValue(Control.TemplateProperty, toggleTemplate);
        toggle.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding("IsDropDownOpen") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent), Mode = System.Windows.Data.BindingMode.TwoWay });
        toggle.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding("SelectionBoxItem") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) }); toggle.SetBinding(ContentControl.ContentTemplateProperty, new System.Windows.Data.Binding("SelectionBoxItemTemplate") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) }); toggle.SetBinding(ContentControl.ContentStringFormatProperty, new System.Windows.Data.Binding("SelectionBoxItemStringFormat") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) }); toggle.SetBinding(ContentControl.ContentTemplateSelectorProperty, new System.Windows.Data.Binding("ItemTemplateSelector") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) }); comboRoot.AppendChild(toggle);
        var popup = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.Popup)); popup.Name = "PART_Popup"; popup.SetValue(System.Windows.Controls.Primitives.Popup.PlacementProperty, System.Windows.Controls.Primitives.PlacementMode.Bottom); popup.SetValue(System.Windows.Controls.Primitives.Popup.AllowsTransparencyProperty, true);
        popup.SetBinding(System.Windows.Controls.Primitives.Popup.IsOpenProperty, new System.Windows.Data.Binding("IsDropDownOpen") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var popupBorder = new FrameworkElementFactory(typeof(Border)); popupBorder.SetResourceReference(Border.BackgroundProperty, "Surface"); popupBorder.SetResourceReference(Border.BorderBrushProperty, "Line"); popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var items = new FrameworkElementFactory(typeof(ItemsPresenter)); popupBorder.AppendChild(items); popup.AppendChild(popupBorder); comboRoot.AppendChild(popup); comboTemplate.VisualTree = comboRoot; combo.Setters.Add(new Setter(Control.TemplateProperty, comboTemplate)); r[typeof(ComboBox)] = combo;
        // Keep native Track semantics and commands for keyboard, wheel and horizontal scrolling.
        const string scrollStyle = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type ScrollBar}">
          <Setter Property="Background" Value="{DynamicResource Bg}"/>
          <Setter Property="Width" Value="14"/>
          <Setter Property="Template"><Setter.Value>
            <ControlTemplate TargetType="{x:Type ScrollBar}">
              <Grid Background="{TemplateBinding Background}">
                <Track x:Name="PART_Track" IsDirectionReversed="True" Orientation="{TemplateBinding Orientation}">
                  <Track.DecreaseRepeatButton><RepeatButton x:Name="PageBefore" Command="ScrollBar.PageUpCommand" Opacity="0" Focusable="False"/></Track.DecreaseRepeatButton>
                  <Track.Thumb><Thumb MinHeight="8" MinWidth="8"><Thumb.Template><ControlTemplate TargetType="Thumb"><Border x:Name="ThumbBorder" Background="{DynamicResource Line}" CornerRadius="3" Margin="4,2"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                  <Track.IncreaseRepeatButton><RepeatButton x:Name="PageAfter" Command="ScrollBar.PageDownCommand" Opacity="0" Focusable="False"/></Track.IncreaseRepeatButton>
                </Track>
              </Grid>
              <ControlTemplate.Triggers><Trigger Property="Orientation" Value="Horizontal">
                <Setter TargetName="PART_Track" Property="IsDirectionReversed" Value="False"/>
                <Setter TargetName="PageBefore" Property="Command" Value="ScrollBar.PageLeftCommand"/>
                <Setter TargetName="PageAfter" Property="Command" Value="ScrollBar.PageRightCommand"/>
              </Trigger></ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value></Setter>
          <Style.Triggers><Trigger Property="Orientation" Value="Horizontal"><Setter Property="Width" Value="Auto"/><Setter Property="Height" Value="14"/></Trigger></Style.Triggers>
        </Style>
        """;
        r[typeof(System.Windows.Controls.Primitives.ScrollBar)] = (Style)System.Windows.Markup.XamlReader.Parse(scrollStyle);
        var input = new Style(typeof(TextBox), control); input.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Surface"))); input.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Line"))); input.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10))); input.Setters.Add(new Setter(TextBox.CaretBrushProperty, new DynamicResourceExtension("Text"))); input.Setters.Add(new Setter(TextBox.SelectionBrushProperty, new DynamicResourceExtension("Accent"))); r[typeof(TextBox)] = input;
    }
}
