using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Shell;

namespace Threadbook.Desktop;

internal sealed class WindowFrame : Grid
{
    private readonly Window owner;
    private readonly ContentPresenter body = new();
    private readonly ContentPresenter caption = new();
    private readonly Button minimize;
    private readonly Button maximize;
    private UIElement? privateCaption;
    private readonly List<(DependencyObject Element, bool Enabled)> tooltips = new();
    public object? BodyContent { get => body.Content; set => body.Content = value; }
    public UIElement? CaptionContent {
        get => privateCaption;
        set { if (ReferenceEquals(privateCaption, value)) return; SuppressCaption(); RestoreToolTips(); privateCaption = value; if (!IsPrivacyLocked) caption.Content = value; }
    }
    public bool IsPrivacyLocked { get; private set; }

    public WindowFrame(Window owner)
    {
        this.owner = owner;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var surface = new CaptionSurface { BorderThickness = new Thickness(0, 0, 0, 1) };
        BindThemeBrush(surface, Border.BackgroundProperty, "ChromeBg", "Bg", new SolidColorBrush(Color.FromRgb(13, 17, 23)));
        surface.SetResourceReference(Border.BorderBrushProperty, "ChromeLine");
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Image { Source = ApplicationBrand.Mark, Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0) });
        var name = new CaptionText { Text = "Threadbook", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        BindThemeBrush(name, TextBlock.ForegroundProperty, "ChromeText", "Text", Brushes.WhiteSmoke); brand.Children.Add(name);
        bar.Children.Add(brand);
        caption.VerticalAlignment = VerticalAlignment.Center; caption.ClipToBounds = true;
        Grid.SetColumn(caption, 1); bar.Children.Add(caption);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        minimize = CaptionButton("Minimize", "—", () => SystemCommands.MinimizeWindow(owner));
        maximize = CaptionButton("Maximize", "□", () => { if (owner.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(owner); else SystemCommands.MaximizeWindow(owner); });
        controls.Children.Add(minimize); controls.Children.Add(maximize); controls.Children.Add(CaptionButton("Close", "×", owner.Close));
        Grid.SetColumn(controls, 2); bar.Children.Add(controls); surface.Child = bar; Children.Add(surface);
        Grid.SetRow(body, 1); Children.Add(body);
        // Every window carries a visible edge so it reads against dark backgrounds.
        var outline = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), IsHitTestVisible = false };
        outline.SetResourceReference(Border.BorderBrushProperty, "Line");
        Grid.SetRowSpan(outline, 2); Children.Add(outline);
        owner.StateChanged += StateChanged; owner.Closed += Closed;
        owner.Loaded += OwnerLoaded;
        UpdateButtons();
    }
    public void SetPrivacyLocked(bool locked)
    {
        if (locked == IsPrivacyLocked) return;
        IsPrivacyLocked = locked;
        if (locked) SuppressCaption(); else { RestoreToolTips(); caption.Content = privateCaption; }
    }
    private void SuppressCaption()
    {
        if (privateCaption is not null && caption.Content is not null) {
            CloseToolTips(privateCaption);
            if (privateCaption.IsKeyboardFocusWithin) { Keyboard.ClearFocus(); FocusManager.SetFocusedElement(owner, null); }
        }
        caption.Content = null;
    }
    private void RestoreToolTips()
    {
        foreach (var entry in tooltips) entry.Element.SetCurrentValue(ToolTipService.IsEnabledProperty, entry.Enabled);
        tooltips.Clear();
    }
    private void CloseToolTips(DependencyObject element)
    {
        if (ToolTipService.GetToolTip(element) is ToolTip tooltip) tooltip.IsOpen = false;
        tooltips.Add((element, ToolTipService.GetIsEnabled(element)));
        element.SetCurrentValue(ToolTipService.IsEnabledProperty, false);
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>()) CloseToolTips(child);
    }
    private Button CaptionButton(string name, string glyph, Action action)
    {
        var button = new CaptionControl { Content = glyph, Width = 46, Height = 40, Padding = new Thickness(0), BorderThickness = new Thickness(0), FontSize = 17, Margin = new Thickness(0) };
        BindThemeBrush(button, Control.ForegroundProperty, "ChromeText", "Text", Brushes.WhiteSmoke);
        BindThemeBrush(button, Control.BackgroundProperty, "ChromeBg", "Bg", new SolidColorBrush(Color.FromRgb(13, 17, 23)));
                var focusStyle = new Style(typeof(Control));
        var focusTemplate = new ControlTemplate(typeof(Control));
        var focusBorder = new FrameworkElementFactory(typeof(Border));
        focusBorder.SetResourceReference(Border.BorderBrushProperty, "Focus"); focusBorder.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        focusBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(4)); focusBorder.SetValue(MarginProperty, new Thickness(3));
        focusTemplate.VisualTree = focusBorder; focusStyle.Setters.Add(new Setter(Control.TemplateProperty, focusTemplate)); button.FocusVisualStyle = focusStyle;
        AutomationProperties.SetName(button, name); button.ToolTip = name;
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.Click += (_, _) => action();
        return button;
    }
    private void OwnerLoaded(object sender, RoutedEventArgs e) => UpdateButtons();
    private void StateChanged(object? sender, EventArgs e) => UpdateButtons();
    private void UpdateButtons()
    {
        minimize.Visibility = owner.ResizeMode == ResizeMode.NoResize ? Visibility.Collapsed : Visibility.Visible;
        maximize.Visibility = owner.ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize ? Visibility.Collapsed : Visibility.Visible;
        bool restored = owner.WindowState == WindowState.Maximized;
        maximize.Content = restored ? "❐" : "□";
        AutomationProperties.SetName(maximize, restored ? "Restore" : "Maximize"); maximize.ToolTip = restored ? "Restore" : "Maximize";
    }
    private void Closed(object? sender, EventArgs e)
    {
        owner.StateChanged -= StateChanged; owner.Closed -= Closed; owner.Loaded -= OwnerLoaded;
        SuppressCaption(); RestoreToolTips(); privateCaption = null;
    }
    private static readonly DependencyProperty PrimaryBackground = DependencyProperty.RegisterAttached("PrimaryBackground", typeof(Brush), typeof(WindowFrame));
    private static readonly DependencyProperty LegacyBackground = DependencyProperty.RegisterAttached("LegacyBackground", typeof(Brush), typeof(WindowFrame));
    private static readonly DependencyProperty PrimaryForeground = DependencyProperty.RegisterAttached("PrimaryForeground", typeof(Brush), typeof(WindowFrame));
    private static readonly DependencyProperty LegacyForeground = DependencyProperty.RegisterAttached("LegacyForeground", typeof(Brush), typeof(WindowFrame));
    internal static void BindThemeBrush(FrameworkElement element, DependencyProperty target, string primaryKey, string legacyKey, Brush fallback)
    {
        bool foreground = target == Control.ForegroundProperty || target == TextBlock.ForegroundProperty;
        var primary = foreground ? PrimaryForeground : PrimaryBackground;
        var legacy = foreground ? LegacyForeground : LegacyBackground;
        element.SetResourceReference(primary, primaryKey); element.SetResourceReference(legacy, legacyKey);
        var binding = new MultiBinding { Converter = new BrushFallback(), ConverterParameter = fallback };
        binding.Bindings.Add(new Binding { Path = new PropertyPath("(0)", primary), RelativeSource = RelativeSource.Self });
        binding.Bindings.Add(new Binding { Path = new PropertyPath("(0)", legacy), RelativeSource = RelativeSource.Self });
        element.SetBinding(target, binding);
    }
    private sealed class BrushFallback : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values.OfType<Brush>().FirstOrDefault() ?? parameter;
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
    private sealed class CaptionSurface : Border
    {
        static CaptionSurface() { BackgroundProperty.OverrideMetadata(typeof(CaptionSurface), new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(13, 17, 23)))); BorderBrushProperty.OverrideMetadata(typeof(CaptionSurface), new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(61, 68, 77)))); }
    }
    private sealed class CaptionText : TextBlock
    {
        static CaptionText() => ForegroundProperty.OverrideMetadata(typeof(CaptionText), new FrameworkPropertyMetadata(Brushes.WhiteSmoke));
    }
    private sealed class CaptionControl : Button
    {
        static CaptionControl() { ForegroundProperty.OverrideMetadata(typeof(CaptionControl), new FrameworkPropertyMetadata(Brushes.WhiteSmoke)); BackgroundProperty.OverrideMetadata(typeof(CaptionControl), new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(13, 17, 23)))); }
    }
}
