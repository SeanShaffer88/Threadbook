using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
namespace Threadbook.Desktop;

internal static class WindowSizing
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    internal static Size Minimum(double desiredWidth, double desiredHeight, double availableWidth, double availableHeight) => new(Math.Min(desiredWidth, Math.Max(1, availableWidth)), Math.Min(desiredHeight, Math.Max(1, availableHeight)));
    internal static void Attach(Window window, double targetWidth, double targetHeight)
    {
        bool adjusting = false;
        void Update(bool recoverPosition)
        {
            if (adjusting || new WindowInteropHelper(window).Handle == 0) return;
            adjusting = true;
            try {
                var handle = new WindowInteropHelper(window).Handle;
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)) return;
                uint dpi = GetDpiForWindow(handle); double scale = (dpi == 0 ? 96 : dpi) / 96d;
                double width = (info.Work.Right - info.Work.Left) / scale, height = (info.Work.Bottom - info.Work.Top) / scale;
                var min = Minimum(targetWidth, targetHeight, width, height);
                window.MinWidth = min.Width; window.MinHeight = min.Height;
                if (window.WindowState != WindowState.Normal) return;
                window.Width = Math.Clamp(window.Width, min.Width, width); window.Height = Math.Clamp(window.Height, min.Height, height);
                if (!recoverPosition) return;
                double left = info.Work.Left / scale, top = info.Work.Top / scale;
                if (double.IsFinite(window.Left)) window.Left = Math.Clamp(window.Left, left, left + Math.Max(0, width - window.Width));
                if (double.IsFinite(window.Top)) window.Top = Math.Clamp(window.Top, top, top + Math.Max(0, height - window.Height));
            } finally { adjusting = false; }
        }
        HwndSource? source = null;
        nint Hook(nint hwnd, int msg, nint wp, nint lp, ref bool handled)
        {
            if (msg is 0x02E0 or 0x007E) window.Dispatcher.BeginInvoke(() => { if (source is not null) Update(msg == 0x007E); }, DispatcherPriority.Loaded);
            return 0;
        }
        void Initialized(object? sender, EventArgs e) { source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle); source?.AddHook(Hook); Update(true); }
        void Location(object? sender, EventArgs e) => Update(false);
        void Loaded(object sender, RoutedEventArgs e) => Update(true);
        void Closed(object? sender, EventArgs e)
        {
            source?.RemoveHook(Hook); source = null;
            window.SourceInitialized -= Initialized; window.LocationChanged -= Location; window.Loaded -= Loaded; window.Closed -= Closed;
        }
        window.SourceInitialized += Initialized; window.LocationChanged += Location; window.Loaded += Loaded; window.Closed += Closed;
    }
}
