using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Threadbook.Core;
using Threadbook.Storage;

namespace Threadbook.Desktop;

public sealed partial class MainWindow
{
    internal bool vaultLocked;
    private long privateExportEpoch;
    internal long CapturePrivateExportTicket() => privateExportEpoch;
    internal bool CompletePrivateExport(long ticket, Action writer)
    {
        Dispatcher.VerifyAccess();
        if (vaultLocked || !store.Files.IsUnlocked) return false;
        if (!ArchiveReady()) return false;
        if (ticket != privateExportEpoch) {
            SetStatus("Export cancelled because the vault session changed. Start a new export.");
            return false;
        }
        // Synchronous writer only: no picker, await or dispatcher pumping after this check.
        writer();
        return true;
    }
    private object? unlockedContent;
    private readonly List<Window> hiddenPrivateWindows = new();
    private readonly DispatcherTimer privateIdle = new() { Interval = TimeSpan.FromSeconds(10) };
    private long lastPrivateInput = Stopwatch.GetTimestamp();
    private int idleMinutes = 15;
    private bool privateLibraryInitialized;
    internal bool ConfirmPrivatePlaintextExport() => ArchiveReady() && PrivateDialogs.ConfirmPlaintext(this, store);

    private void InitializePrivateLibrary(DockPanel root)
    {
        if (privateLibraryInitialized) return; privateLibraryInitialized = true;
        InputManager.Current.PreProcessInput += TrackPrivateInput;
        privateIdle.Tick += (_, _) => CheckPrivateIdle();
        Loaded += (_, _) => privateIdle.Start();
        Closed += (_, _) => { privateIdle.Stop(); InputManager.Current.PreProcessInput -= TrackPrivateInput; };
    }
    private bool CheckPrivateIdle() => !vaultLocked && store.Files.IsEncrypted && idleMinutes > 0 && Stopwatch.GetElapsedTime(lastPrivateInput) >= TimeSpan.FromMinutes(idleMinutes) && TryLockVault();
    private void TrackPrivateInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is KeyEventArgs or MouseButtonEventArgs or MouseWheelEventArgs || e.StagingItem.Input is MouseEventArgs { RoutedEvent: var routed } && routed == Mouse.MouseMoveEvent)
            lastPrivateInput = Stopwatch.GetTimestamp();
    }
    internal bool TryLockVault()
    {
        if (vaultLocked || !store.Files.IsEncrypted) return false;
        var windows = Application.Current.Windows.Cast<Window>().Where(w => w != this && IsPrivateOwned(w)).ToArray();
        foreach (var editor in windows.OfType<NoteEditor>()) if (!editor.PrepareForVaultLock()) { foreach (var prior in windows.OfType<NoteEditor>()) prior.ResumeAfterVaultUnlock(); SetStatus("Vault remains unlocked: a draft could not be retained. Retry or explicitly discard it.", true); return false; }
        var inlineFlush = DraftCoordinator.FlushAll(); if (!inlineFlush.Success) { foreach (var prior in windows.OfType<NoteEditor>()) prior.ResumeAfterVaultUnlock(); SetStatus(inlineFlush.Error!.Message, true); return false; }
        try {
            // A reply draft remains in the hidden view; encrypted recovery protects saved editor drafts.
            searchDelay.Stop(); reminderTimer.Stop(); LibraryRemindersPaused = true;
            SuspendWikiLinks();
            hiddenPrivateWindows.Clear(); foreach (var window in windows.Where(w => w.IsVisible && w is not CommandPalette && w != wikiChooser)) { window.Hide(); hiddenPrivateWindows.Add(window); }
            unlockedContent = frame.BodyContent; frame.SetPrivacyLocked(true);
            var cover = new StackPanel { Margin = new Thickness(50), VerticalAlignment = VerticalAlignment.Center };
            cover.Children.Add(new TextBlock { Text = "Vault locked", FontSize = 30, Foreground = Brushes.White });
            cover.Children.Add(new TextBlock { Text = "Files are encrypted at rest. Managed memory erasure is not guaranteed.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White });
            cover.Children.Add(Btn("Unlock vault", UnlockPrivateVault));
            frame.BodyContent = new Border { Background = Brushes.Black, Child = cover };
            privateExportEpoch = checked(privateExportEpoch + 1);
            store.Lock(); vaultLocked = true; commandPalette?.Close(); UpdateReplyCommand(); return true;
        } catch (Exception ex) when (ex is DataException or IOException) {
            if (store.Files.IsUnlocked) { frame.BodyContent = unlockedContent; frame.SetPrivacyLocked(false); draftCoordinator?.ResumeAll(); foreach (var editor in windows.OfType<NoteEditor>()) editor.ResumeAfterVaultUnlock(); foreach (var w in hiddenPrivateWindows) w.Show(); hiddenPrivateWindows.Clear(); LibraryRemindersPaused = false; reminderTimer.Start(); SetStatus("Lock failed; vault remains unlocked.", true); }
            return false;
        }
    }
    private bool IsPrivateOwned(Window window) { for (Window? owner = window.Owner; owner is not null; owner = owner.Owner) if (owner == this) return true; return false; }
    private void UnlockPrivateVault()
    {
        if (!vaultLocked) return;
        if (!PrivateDialogs.Password(this, "Unlock Threadbook vault", false, password => { store.Unlock(password); return true; }, out bool _)) return;
        ResumePrivateVault();
    }
    private void ResumePrivateVault()
    {
        store.AssertLiveOwner();
        vaultLocked = false; draftCoordinator?.ResumeAll(); UpdateReplyCommand(); frame.BodyContent = unlockedContent; frame.SetPrivacyLocked(false); unlockedContent = null;
        try { archiveMutations.Reload(); ReloadLibrary(); RefreshArchiveWarning(); }
        catch (Exception ex) when (ArchiveError(ex)) { SetStatus("Archive reload failed; original files preserved. " + ex.Message, true); RefreshArchiveWarning(); }
        foreach (var window in hiddenPrivateWindows) {
            if (window is NoteEditor editor) editor.ResumeAfterVaultUnlock();
            // Hide ends WPF's modal loop. Reopen modally so DialogResult-based Save stays valid.
            Dispatcher.BeginInvoke(new Action(() => { if (!vaultLocked && IsVisible) { try { window.ShowDialog(); if (!vaultLocked) { ReloadLibrary(); Refresh(); } } catch (Exception ex) when (ArchiveError(ex)) { SetStatus(ex.Message, true); } } }));
        }
        hiddenPrivateWindows.Clear(); lastPrivateInput = Stopwatch.GetTimestamp(); ReloadLibrary(); RefreshDetail(); LibraryRemindersPaused = false; reminderTimer.Start();
    }
    private void PrivateAction(Action action)
    {
        if (!ArchiveReady()) return;
        try { action(); } catch (Exception ex) when (ex is DataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException) { SetStatus("Private library operation failed: " + ex.Message, true); }
    }
    private void CreatePrivateCopy() => PrivateAction(() => {
        if (MessageBox.Show(this, "Create a NEW encrypted copy. Your ORIGINAL PLAINTEXT FOLDER AND ITS BACKUPS REMAIN unchanged and readable. Keep your password safe; there is no recovery. Continue?", "Encrypted copy", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        string? destination = NewPrivateDirectory("Choose the parent for a NEW encrypted vault"); if (destination is null) return;
        if (PrivateDialogs.Password(this, "Create encrypted copy", true, password => { using var copy = VaultCopies.CreateEncryptedCopy(store, destination, password); return true; }, out bool _)) {
            SetStatus("Encrypted copy verified. Original folder and backups remain unchanged.");
            if (MessageBox.Show(this, "Open the verified encrypted copy in a separate window?", "Encrypted copy", MessageBoxButton.YesNo) == MessageBoxResult.Yes) OpenPrivateDirectory(destination);
        }
    });
    private string? NewPrivateDirectory(string title)
    {
        var parent = new OpenFolderDialog { Title = title }; if (parent.ShowDialog(this) != true) return null;
        // A unique child is always absent; restore/copy never targets the selected existing folder.
        return Path.Combine(parent.FolderName, "Threadbook-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
    }
    private void OpenPrivateDirectory(string directory) => PrivateAction(() => {
        if (!TryLeave(LeaveReason.VaultSwitch)) return;
        if (!Directory.Exists(directory) || (!JsonStore.IsEncryptedDirectory(directory) && !File.Exists(Path.Combine(directory, "threadbook.json")))) throw new IOException("Select an existing Threadbook directory.");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(directory); Process.Start(start)?.Dispose();
    });
    private void BackupPrivateLibrary() => PrivateAction(() => {
        var ticket = CapturePrivateExportTicket();
        var save = new SaveFileDialog { Filter = "Threadbook portable backup|*.threadbook.zip", FileName = "Threadbook-backup.threadbook.zip", OverwritePrompt = true };
        if (save.ShowDialog(this) != true) return;
        if (CompletePrivateExport(ticket, () => WritePrivateBackup(save.FileName))) SetStatus(store.Files.IsEncrypted ? "Encrypted backup saved as ciphertext." : "Portable plaintext backup saved.");
    });
    private void WritePrivateBackup(string destination)
    {
        if (ExportSafety.IsProtected(destination, dataDir)) throw new IOException("Choose a backup destination outside the live vault without filesystem links.");
        var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, ".threadbook-backup-" + Guid.NewGuid().ToString("N") + ".tmp");
        try {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { PortableLibrary.CreateBackup(store, stream); stream.Flush(true); }
            if (ExportSafety.IsProtected(destination, dataDir)) throw new IOException("Backup destination changed.");
            if (File.Exists(destination)) File.Replace(temp, destination, null); else File.Move(temp, destination);
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private void RestorePrivateLibrary() => PrivateAction(() => {
        var open = new OpenFileDialog { Filter = "Threadbook portable backup|*.zip" }; if (open.ShowDialog(this) != true) return;
        string? destination = NewPrivateDirectory("Choose parent for NEW restored directory"); if (destination is null) return;
        // Explicit choice avoids probing a password through logs or command arguments.
        bool encrypted = MessageBox.Show(this, "Is this an encrypted backup? Choose Yes to enter its password.", "Restore backup", MessageBoxButton.YesNoCancel) switch { MessageBoxResult.Yes => true, MessageBoxResult.No => false, _ => throw new IOException("Restore cancelled.") };
        void Restore(string? password) { using var stream = File.OpenRead(open.FileName); PortableLibrary.Restore(stream, destination, password); }
        if (encrypted) {
            if (!PrivateDialogs.Password(this, "Unlock backup for validation", false, password => { string transient = new(password); try { Restore(transient); return true; } finally { transient = string.Empty; } }, out bool _)) return;
        } else Restore(null);
        SetStatus("Backup validated and restored to a NEW directory. Current vault unchanged.");
        if (MessageBox.Show(this, "Open the restored library?", "Restore complete", MessageBoxButton.YesNo) == MessageBoxResult.Yes) OpenPrivateDirectory(destination);
    });
    private void OpenAttachments() => PrivateAction(() => {
        if (selectedId is not Guid noteId || !app.State.Notes.Any(n => n.Id == noteId)) { SetStatus("Select a saved note before managing attachments.", true); return; }
        var current = new LibraryService(new LibraryStore(store), () => app.State);
        var attachments = new AttachmentStore(store, current);
        var dialog = new Window { Owner = this, Title = "Attachments · saved note", Width = 720, Height = 620, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White, Foreground = Brushes.Black };
        ApplicationBrand.Apply(dialog);
        WindowSizing.Attach(dialog, 600, 480);
        var panel = new DockPanel { Margin = new Thickness(18) };
        var outline = new Border { BorderThickness = new Thickness(1), Child = panel };
        outline.SetResourceReference(Border.BorderBrushProperty, "Line");
        dialog.Content = outline;
        var controls = new WrapPanel(); DockPanel.SetDock(controls, Dock.Top); panel.Children.Add(controls);
        var message = new TextBlock { Text = "PNG/JPEG can be previewed. Other files stay opaque; no attachment is executed.", TextWrapping = TextWrapping.Wrap }; DockPanel.SetDock(message, Dock.Bottom); panel.Children.Add(message);
        var list = new ListBox { DisplayMemberPath = "DisplayName", MaxHeight = 190 }; DockPanel.SetDock(list, Dock.Top); panel.Children.Add(list);
        var image = new Image { Stretch = Stretch.Uniform, MaxHeight = 300 }; panel.Children.Add(image);
        void Load() { list.ItemsSource = current.State.Attachments.Where(a => a.NoteId == noteId).ToArray(); image.Source = null; }
        void Attempt(Action action) { if (!ArchiveReady()) return; try { action(); } catch (Exception ex) when (ex is DataException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException or System.Runtime.InteropServices.COMException) { message.Text = "Attachment operation failed: " + ex.Message; } }
        void Button(string text, Action action) { var button = Btn(text, () => Attempt(action)); controls.Children.Add(button); }
        Button("Add file / image…", () => {
            var open = new OpenFileDialog(); if (open.ShowDialog(dialog) != true || !ArchiveReady()) return;
            using var stream = File.OpenRead(open.FileName); var extension = Path.GetExtension(open.FileName).ToLowerInvariant();
            var stage = attachments.Stage(noteId, stream, Path.GetFileName(open.FileName), extension switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", _ => "application/octet-stream" });
            Outcome outcome;
            try { outcome = attachments.AddToNote(stage); }
            catch { attachments.Discard(stage); throw; }
            if (!outcome.Success) throw new IOException(outcome.Error!.Message + " " + outcome.Warning);
            message.Text = "Attachment added to saved note. Select it and Preview image to view it.";
            Load(); ReloadLibrary(); Refresh();
        });
        Button("Preview image", () => { if (list.SelectedItem is not AttachmentInfo info) return; if (info.MediaType is not ("image/png" or "image/jpeg")) { message.Text = "This file is opaque. Use Save As to export it."; return; } using var stream = attachments.OpenRead(info.Id, noteId); image.Source = PrivateDialogs.DecodeImage(stream); });
        Button("Save As…", () => {
            if (list.SelectedItem is not AttachmentInfo info || !PrivateDialogs.ConfirmPlaintext(dialog, store)) return;
            var save = new SaveFileDialog { FileName = info.DisplayName, OverwritePrompt = true }; if (save.ShowDialog(dialog) != true || !ArchiveReady()) return;
            if (ExportSafety.IsProtected(save.FileName, dataDir)) throw new IOException("Choose a destination outside the live vault without filesystem links.");
            var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(save.FileName))!, ".threadbook-attachment-" + Guid.NewGuid() + ".tmp");
            try { using (var input = attachments.OpenRead(info.Id, noteId)) using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { input.CopyTo(output); output.Flush(true); } if (ExportSafety.IsProtected(save.FileName, dataDir)) throw new IOException("Export destination changed."); if (File.Exists(save.FileName)) File.Replace(temp, save.FileName, null); else File.Move(temp, save.FileName); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            message.Text = "Attachment exported explicitly.";
        });
        Button("Remove", () => { if (list.SelectedItem is not AttachmentInfo info || MessageBox.Show(dialog, "Remove this attachment?", "Remove attachment", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; if (!ArchiveReady()) return; var result = attachments.Remove(info.Id); if (!result.Success) throw new IOException(result.Error!.Message); Load(); ReloadLibrary(); Refresh(); });
        list.SelectionChanged += (_, _) => { image.Source = null; if (list.SelectedItem is AttachmentInfo info) message.Text = $"{info.DisplayName} · {info.MediaType} · {info.Length:N0} bytes · SHA256 {info.Sha256}"; };
        dialog.Closed += (_, _) => image.Source = null; Load(); dialog.ShowDialog();
    });
    public void RunPrivateLibrarySmokeTest()
    {
        if (!privateLibraryInitialized || !new[] { "Encrypted copy…", "Attachments…", "Backup…", "Restore backup…", "Open vault…" }.All(uiCommands.ContainsKey)) throw new Exception("Private commands not initialized.");
        string destination = Path.Combine(Path.GetDirectoryName(dataDir)!, "private-copy-" + Guid.NewGuid().ToString("N"));
        char[] password = "synthetic-private-fixture-password".ToCharArray();
        try {
            using var encrypted = VaultCopies.CreateEncryptedCopy(store, destination, password);
            var expected = encrypted.Load(); encrypted.Lock();
            if (encrypted.Files.IsUnlocked) throw new Exception("Lock retained key.");
            bool rejected = false; try { encrypted.Unlock("wrong-fixture-password".AsSpan()); } catch (DataException) { rejected = true; }
            if (!rejected || encrypted.Files.IsUnlocked) throw new Exception("Wrong password accepted.");
            encrypted.Unlock(password); if (encrypted.Load().Revision != expected.Revision) throw new Exception("Encrypted unlock roundtrip failed.");
            using var archive = new MemoryStream(); PortableLibrary.CreateBackup(encrypted, archive); if (archive.Length == 0) throw new Exception("Empty backup.");
        } finally { Array.Clear(password); }
    }
}
