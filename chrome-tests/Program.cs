using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Threadbook.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Console.Error.WriteLine(e.Exception); e.Handled = true; Environment.ExitCode = 1; app.Shutdown(1); };
        Window? window = null;
        try {
            window = new Window { Title = "Threadbook", Width = 640, Height = 480, Content = new TextBox { Text = "body" } };
            var body = window.Content;
            Check(WindowChromeHelper.GetFrame(window) is null, "lookup is inert");
            var frame = WindowChromeHelper.Attach(window);
            Check(ReferenceEquals(body, frame.BodyContent) && ReferenceEquals(frame, WindowChromeHelper.Attach(window)), "attachment preserves identity and is idempotent");
            var tooltip = new ToolTip { Content = "private tooltip" };
            var caption = new TextBox { Text = "private query", ToolTip = tooltip };
            frame.CaptionContent = caption;
            WindowSizing.Attach(window, 800, 720);
            window.Show(); Pump(); caption.Focus(); Pump();
            Check(window.MinWidth <= 800 && window.MinHeight <= 720 && window.ActualWidth >= window.MinWidth && window.ActualHeight >= window.MinHeight, "monitor-clamped managed minima");
            Check(caption.IsKeyboardFocusWithin, "caption receives focus before suppression");
            tooltip.IsOpen = true;
            frame.SetPrivacyLocked(true);
            Check(!tooltip.IsOpen, "privacy synchronously closes caption tooltip"); Pump();
            Check(frame.IsPrivacyLocked && PresentationSource.FromVisual(caption) is null && LogicalTreeHelper.GetParent(caption) is null && !caption.IsKeyboardFocusWithin, "privacy detaches visual/logical/focus");
            Check(!AutomationContains(new WindowAutomationPeer(window), caption), "private caption absent from active automation tree");
            frame.SetPrivacyLocked(false); Pump();
            Check(ReferenceEquals(frame.CaptionContent, caption) && PresentationSource.FromVisual(caption) is not null, "unlock restores same caption");
                        Check(ToolTipService.GetIsEnabled(caption), "unlock restores tooltip enabled state");
            var surface = Descendants(frame).OfType<Border>().First();
            var safeBrush = (SolidColorBrush)surface.Background;
            Check(safeBrush.Color.R < 40, "caption dark fallback before theme");
            app.Resources["Bg"] = Brushes.Ivory; app.Resources["Text"] = Brushes.Black; Pump();
            Check(Equals(surface.Background, Brushes.Ivory), "legacy Bg token fallback resolves");
            app.Resources["ChromeBg"] = Brushes.White; app.Resources["ChromeText"] = Brushes.Black; Pump();
            Check(Equals(surface.Background, Brushes.White), "new chrome theme token overrides dynamically");
            Check(Descendants(frame).OfType<Button>().All(b => b.FocusVisualStyle is not null && b.Width == 46 && b.Height == 40 && WindowChrome.GetIsHitTestVisibleInChrome(b)), "accessible caption hit areas and focus visuals");
            var chrome = WindowChrome.GetWindowChrome(window);
            Check(chrome.CaptionHeight == 40 && chrome.ResizeBorderThickness == new Thickness(6) && chrome.GlassFrameThickness == new Thickness(0) && !window.AllowsTransparency && window.WindowStyle == WindowStyle.None, "native chrome settings");
            InvokeButton(frame, "Maximize"); Pump(); Check(window.WindowState == WindowState.Maximized, "maximize command");
                        var handle = new WindowInteropHelper(window).Handle;
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            Check(GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor) && GetWindowRect(handle, out var bounds) && bounds.Left >= monitor.Work.Left - 1 && bounds.Top >= monitor.Work.Top - 1 && bounds.Right <= monitor.Work.Right + 1 && bounds.Bottom <= monitor.Work.Bottom + 1, "maximized native bounds respect monitor work area");
            InvokeButton(frame, "Restore"); Pump(); Check(window.WindowState == WindowState.Normal, "restore command");
            InvokeButton(frame, "Minimize"); Pump(); Check(window.WindowState == WindowState.Minimized, "minimize command");
            SystemCommands.RestoreWindow(window); Pump();
            bool cancel = true; window.Closing += (_, e) => e.Cancel = cancel;
            InvokeButton(frame, "Close"); Pump(); Check(window.IsVisible, "close honors cancellation");
            cancel = false; InvokeButton(frame, "Close"); Pump(); Check(!window.IsVisible, "close succeeds after cancellation clears"); window = null;
            window = new Window { Title = "Threadbook", ResizeMode = ResizeMode.NoResize, Width = 450, Height = 280 };
            WindowFrame.BindThemeBrush(window, Window.BackgroundProperty, "Bg", "Bg", Brushes.Black); WindowFrame.BindThemeBrush(window, Window.ForegroundProperty, "Text", "Text", Brushes.WhiteSmoke);
            app.Resources.Clear(); WindowChromeHelper.Attach(window); window.Show(); Pump(); Check(window.ResizeMode == ResizeMode.NoResize && Descendants((WindowFrame)window.Content).OfType<Button>().Count(b => b.Visibility == Visibility.Visible) == 1, "password only exposes Close and preserves NoResize");
            Check(Equals(window.Background, Brushes.Black) && Equals(window.Foreground, Brushes.WhiteSmoke), "ownerless prompt body fallback contrast");
            var password = new PasswordBox(); ((WindowFrame)window.Content).BodyContent = password; password.ApplyTemplate(); Pump();
            Check(Contrast(password.Foreground, password.Background) >= 4.5, "native password input contrast before theme");
            CheckDialogContrast(app, window, password);
            CheckDialogActionGuard(window);
            app.Resources["Bg"] = Brushes.White; app.Resources["Text"] = Brushes.Black; Pump(); Check(Equals(window.Background, Brushes.White) && Equals(window.Foreground, Brushes.Black), "prompt body follows later light theme");
            Console.WriteLine("PASS: isolated shared chrome fixture"); return Environment.ExitCode;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { window?.Close(); app.Shutdown(); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static void CheckDialogContrast(Application app, Window owner, PasswordBox password)
    {
        var dialog = new LibraryDialog(owner, "Contrast fixture");
        var id = Guid.NewGuid(); var choice = dialog.Choice("Fixture choice", new[] { ((Guid?)id, "Selected fixture") }, id);
        dialog.Show(); Pump();
        try {
            foreach (var light in new[] { false, true }) {
                var foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#182128" : "#F1F3F5"));
                var background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#FFFFFF" : "#171B1E"));
                app.Resources["Text"] = foreground; app.Resources["Surface"] = background;
                app.Resources["Bg"] = background; choice.SetResourceReference(Control.BackgroundProperty, "Surface"); Pump(); choice.ApplyTemplate(); password.ApplyTemplate();
                Check(Equals(choice.Foreground, foreground) && Contrast(choice.Foreground, choice.Background) >= 4.5, $"{(light ? "Light" : "Dark")} choice dynamic text and surface contrast");
                Check(Equals(choice.SelectedValue, id) && choice.Items.Count == 1, "choice selection and values preserved");
                Check(Contrast(password.Foreground, password.Background) >= 4.5, $"{(light ? "Light" : "Dark")} native password input contrast");
            }
        } finally { dialog.Close(); }
    }
    private static void CheckDialogActionGuard(Window owner)
    {
        var host = new DialogHostWindow { Owner = owner };
        host.Show();
        var intermediate = new Window { Owner = host };
        intermediate.Show();
        var dialog = new LibraryDialog(intermediate, "Action guard fixture");
        int actions = 0;
        dialog.Action("Run", () => actions++);
        var button = dialog.Fields.Children.OfType<Button>().Single();
        try {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(actions == 0, "owner chain blocks ineligible host action");
            host.Allowed = true;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(actions == 1, "owner chain permits eligible host action");
            host.Allowed = false;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(actions == 1, "owner chain rechecks eligibility on every action");
            dialog.Owner = owner;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(actions == 2, "ordinary window owner preserves action behavior");
        } finally { dialog.Close(); intermediate.Close(); host.Close(); }
    }
    private sealed class DialogHostWindow : Window, ILibraryDialogHost
    {
        internal bool Allowed;
        bool ILibraryDialogHost.AllowLibraryDialogAction() => Allowed;
    }
    private static double Contrast(Brush foreground, Brush background)
    {
        static double Luminance(Brush brush) { var c = ((SolidColorBrush)brush).Color; static double Linear(byte b) { double v = b / 255.0; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); } return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B); }
        var a = Luminance(foreground); var b = Luminance(background); Console.WriteLine($"Contrast: {a:F4}/{b:F4}"); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private static bool AutomationContains(AutomationPeer peer, UIElement target) => peer is UIElementAutomationPeer ui && ReferenceEquals(ui.Owner, target) || (peer.GetChildren()?.Any(child => AutomationContains(child, target)) ?? false);
    private static void InvokeButton(WindowFrame frame, string name)
    {
        var button = Descendants(frame).OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
}
