using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Threadbook.Core;
using Threadbook.Storage;

namespace Threadbook.Desktop;

internal static class PrivateDialogs
{
    internal delegate T PasswordAction<T>(ReadOnlySpan<char> password);
    internal static bool Password<T>(Window? owner, string title, bool confirm, PasswordAction<T> action, out T result)
    {
        T value = default!; bool accepted = false;
        var window = new Window { Title = title, Width = 450, Height = confirm ? 350 : 280, WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = new SolidColorBrush(Color.FromRgb(14, 17, 19)), Foreground = Brushes.WhiteSmoke };
        ApplicationBrand.Apply(window);
        if (owner is not null) window.Owner = owner;
        var panel = new StackPanel { Margin = new Thickness(24) }; window.Content = panel;
        WindowFrame.BindThemeBrush(window, Window.BackgroundProperty, "Bg", "Bg", new SolidColorBrush(Color.FromRgb(14, 17, 19)));
        WindowFrame.BindThemeBrush(window, Window.ForegroundProperty, "Text", "Text", Brushes.WhiteSmoke);
        WindowChromeHelper.Attach(window);
        panel.Children.Add(new TextBlock { Text = confirm ? "Choose a password (at least 12 characters). Keep it safe: there is no password recovery." : "Enter the vault password. Wrong passwords never open plaintext data.", TextWrapping = TextWrapping.Wrap });
        var password = new PasswordBox { MaxLength = 1024, Margin = new Thickness(0, 12, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(password, "Vault password"); panel.Children.Add(password);
        var repeat = new PasswordBox { MaxLength = 1024, Margin = new Thickness(0, 0, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(repeat, "Confirm vault password"); if (confirm) panel.Children.Add(repeat);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.OrangeRed }; panel.Children.Add(error);
        var submit = new Button { Content = confirm ? "Create encrypted copy" : "Unlock", MinHeight = 34, Margin = new Thickness(0, 8, 0, 0), IsDefault = true }; panel.Children.Add(submit);
        submit.Click += (_, _) => {
            char[]? chars = null, other = null;
            try {
                chars = ReadPassword(password); other = confirm ? ReadPassword(repeat) : null;
                if (chars.Length == 0 || (confirm && (chars.Length < 12 || !chars.AsSpan().SequenceEqual(other)))) { error.Text = "Passwords must match and contain at least 12 characters."; return; }
                value = action(chars); accepted = true; window.DialogResult = true;
            } catch (Exception ex) when (ex is DataException or IOException or UnauthorizedAccessException or CryptographicException or ArgumentException) { error.Text = "Operation failed. Check your password and selected directory. No plaintext fallback was used."; }
            finally { if (chars is not null) Array.Clear(chars); if (other is not null) Array.Clear(other); password.Clear(); repeat.Clear(); }
        };
        window.Closed += (_, _) => { password.Clear(); repeat.Clear(); }; window.Loaded += (_, _) => password.Focus();
        window.ShowDialog(); result = value; return accepted;
    }
    private static char[] ReadPassword(PasswordBox box)
    {
        using var secure = box.SecurePassword; IntPtr buffer = Marshal.SecureStringToGlobalAllocUnicode(secure);
        try { var chars = new char[secure.Length]; Marshal.Copy(buffer, chars, 0, chars.Length); return chars; }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(buffer); }
    }
    internal static bool ConfirmPlaintext(Window owner, JsonStore store) => !store.Files.IsEncrypted || MessageBox.Show(owner, "This exports readable plaintext outside the encrypted vault. Anyone with access to the exported file or its backups can read it. Continue?", "Plaintext export", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    internal static BitmapSource DecodeImage(Stream input)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[81920]; int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { if (bytes.Length + count > 25 * 1024 * 1024) throw new IOException("Image exceeds the byte limit."); bytes.Write(buffer, 0, count); }
        bytes.Position = 0;
        var decoder = BitmapDecoder.Create(bytes, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder is not PngBitmapDecoder && decoder is not JpegBitmapDecoder) throw new IOException("Only PNG and JPEG previews are supported.");
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > 16384 || frame.PixelHeight > 16384 || (long)frame.PixelWidth * frame.PixelHeight > 16_000_000) throw new IOException("Image dimensions exceed the preview limit.");
        bytes.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = Math.Min(frame.PixelWidth, 2048); image.StreamSource = bytes; image.EndInit(); image.Freeze(); return image;
    }
}
