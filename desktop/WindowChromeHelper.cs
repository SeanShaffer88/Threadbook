using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace Threadbook.Desktop;

internal static class WindowChromeHelper
{
    private static readonly DependencyProperty FrameProperty = DependencyProperty.RegisterAttached("Frame", typeof(WindowFrame), typeof(WindowChromeHelper));
    public static WindowFrame? GetFrame(Window window) => (WindowFrame?)window.GetValue(FrameProperty);
    public static WindowFrame Attach(Window window)
    {
        if (GetFrame(window) is { } existing) return existing;
        if (window.Content is WindowFrame) throw new InvalidOperationException("Window already contains an unregistered frame.");
        var content = window.Content;
        var frame = new WindowFrame(window);
        window.Content = null;
        frame.BodyContent = content;
        window.Content = frame; window.SetValue(FrameProperty, frame);
        window.WindowStyle = WindowStyle.None; window.AllowsTransparency = false;
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = 40, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(5), UseAeroCaptionButtons = false });
        ApplicationBrand.Apply(window);
        HwndSource? source = null;
        void Initialized(object? sender, EventArgs e) { source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle); source?.AddHook(WorkAreaHook); }
        void Closed(object? sender, EventArgs e) { source?.RemoveHook(WorkAreaHook); source = null; window.SourceInitialized -= Initialized; window.Closed -= Closed; }
        window.SourceInitialized += Initialized; window.Closed += Closed;
        if (new WindowInteropHelper(window).Handle != 0) Initialized(window, EventArgs.Empty);
        return frame;
    }
    // WM_GETMINMAXINFO uses physical pixels. Keep monitor-relative positions in pixels;
    // WPF WindowSizing independently converts the work area to DIPs for managed minima.
    private static nint WorkAreaHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0024) return 0;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor)) return 0;
        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MaxPosition.X = monitor.Work.Left - monitor.Monitor.Left;
        info.MaxPosition.Y = monitor.Work.Top - monitor.Monitor.Top;
        info.MaxSize.X = monitor.Work.Right - monitor.Work.Left;
        info.MaxSize.Y = monitor.Work.Bottom - monitor.Work.Top;
        Marshal.StructureToPtr(info, lParam, false); handled = true;
        return 0;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
