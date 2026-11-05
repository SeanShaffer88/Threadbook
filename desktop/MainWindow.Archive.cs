using System.IO;
using System.Windows;
using System.Windows.Controls;
using Threadbook.Core;
using Threadbook.Storage;

namespace Threadbook.Desktop;

public sealed partial class MainWindow
{
    private readonly ArchiveMutationService archiveMutations;
    private Func<bool>? confirmArchiveRemovalForTest = null;
    internal bool AllowLibraryDialogAction() => ArchiveReady();
    private readonly TextBlock archiveWarning = new() { TextWrapping = TextWrapping.Wrap };
    private static bool ArchiveError(Exception ex) => ex is DataException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException;
    private bool ArchiveUnlocked => !vaultLocked && store.Files.IsUnlocked;
    private bool ArchivePending => store.Files.Exists("archive-operation.json");
    private void RefreshArchiveWarning()
    {
        if (!ArchiveUnlocked) return;
        try { archiveWarning.Text = ArchivePending ? "An archive operation is pending. Writes, editors, imports and plaintext exports are blocked. Open History or Trash to Retry completion or Cancel prepared operation." : ""; }
        catch (Exception ex) when (ArchiveError(ex)) { archiveWarning.Text = "Archive unavailable; original bytes preserved. " + ex.Message; }
    }
    private bool ArchiveReady()
    {
        Dispatcher.VerifyAccess();
        if (!ArchiveUnlocked) return false;
        try {
            if (!ArchivePending) { _ = archiveMutations.ListTrash(); return true; }
            RefreshArchiveWarning(); SetStatus(archiveWarning.Text, true); return false;
        } catch (Exception ex) when (ArchiveError(ex)) { archiveWarning.Text = "Archive unavailable; original bytes preserved. " + ex.Message; SetStatus(archiveWarning.Text, true); return false; }
    }
    private bool CompleteArchiveMutation(Outcome result)
    {
        // The canonical file may have committed even when a later metadata step failed.
        // Reload the same service, retaining the journal and the visible archive entry.
        try { archiveMutations.Reload(); ReloadLibrary(); }
        catch (Exception ex) when (ArchiveError(ex)) { RefreshArchiveWarning(); SetStatus("Reload failed; operation may be pending: " + ex.Message, true); return false; }
        RefreshArchiveWarning();
        if (!result.Success) { Refresh(); SetStatus(result.Error!.Message + (ArchivePending ? " Open History / Trash to complete the pending operation." : ""), true); return false; }
        SetStatus(result.Warning ?? "Saved on this device."); return true;
    }
    private bool RestoreArchiveEntry(Guid entryId, Guid? destination)
    {
        if (!TryLeave(LeaveReason.Restore)) return false;
        if (!ArchiveReady()) return false;
        try { return CompleteArchiveMutation(archiveMutations.Restore(archiveMutations.PrepareRestore(entryId, destination, app.State.Revision, archiveMutations.LibraryState.Revision))); }
        catch (Exception ex) when (ArchiveError(ex)) { SetStatus("Restore rejected: " + ex.Message, true); return false; }
    }
    private bool DeleteArchiveEntry(Window owner, Guid entryId)
    {
        if (!ArchiveReady()) return false;
        if (!(confirmArchiveRemovalForTest?.Invoke() ?? MessageBox.Show(owner, "Permanently remove this archived entry and its retained payloads? This cannot be undone.", "Permanent removal", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)) return false;
        if (!ArchiveReady()) return false;
        return CompleteArchiveMutation(archiveMutations.PermanentlyDeleteArchive(entryId));
    }
    private LibraryDialog? BuildArchiveDialog(bool trash)
    {
        if (!ArchiveUnlocked) return null;
        var d = new LibraryDialog(this, trash ? "Trash" : "History") { Width = 820, Height = 760 };
        var message = Label("", 13); d.Fields.Children.Add(message);
        void Attempt(Action action) {
            if (!ArchiveUnlocked) return;
            try { action(); } catch (Exception ex) when (ArchiveError(ex)) { message.Text = ex.Message; SetStatus(ex.Message, true); }
        }
        d.Fields.Children.Add(Label("History retains previous content; restoring first saves the current version. Trash retains full notes, replies, tags, tasks, attachments and notebook cascades. Nothing is purged automatically.", 13));
        d.Fields.Children.Add(Label("Capacity: 20 versions per note, 5,000 entries and 512 MiB total. When full, changes stop. Export needed content, then explicitly remove selected entries.", 12, false, "Muted"));
        var pending = Label("", 13); d.Fields.Children.Add(pending);
        var list = new ListBox { MinHeight = 100, MaxHeight = 180, DisplayMemberPath = "Title" }; d.Fields.Children.Add(list);
        list.SetResourceReference(Control.BackgroundProperty, "Surface"); list.SetResourceReference(Control.ForegroundProperty, "Text"); list.SetResourceReference(Control.BorderBrushProperty, "Line");
        var rowStyle = new Style(typeof(ListBoxItem)); rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Text"))); rowStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 6, 10, 6)));
        var rowTemplate = new ControlTemplate(typeof(ListBoxItem)); var rowBorder = new FrameworkElementFactory(typeof(Border));
        rowBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty)); rowBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty)); rowBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var rowContent = new FrameworkElementFactory(typeof(ContentPresenter)); rowContent.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty)); rowBorder.AppendChild(rowContent); rowTemplate.VisualTree = rowBorder; rowStyle.Setters.Add(new Setter(Control.TemplateProperty, rowTemplate));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true }; selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Selected"))); selected.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Accent"))); selected.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(3, 0, 0, 0))); rowStyle.Triggers.Add(selected); list.ItemContainerStyle = rowStyle;
        var preview = d.Text("Version / Trash preview", "", 100000, true); preview.IsReadOnly = true;
        var destination = d.Choice("Restore destination", new[] { ((Guid?)null, trash ? "Original notebooks (recreate missing notebooks)" : "Current notebook") }.Concat(app.State.Notebooks.Select(b => ((Guid?)b.Id, "n/" + b.Name))));
        destination.SelectedIndex = 0;
        Button restore = null!;
        restore = Btn(trash ? "Restore selected entry" : "Restore version", () => Attempt(() => {
            if (list.SelectedItem is not ArchiveRow row || !ArchiveReady()) return;
            if (RestoreArchiveEntry(row.Entry.Id, destination.SelectedValue as Guid?)) { Refresh(); Load(); message.Text = "Restored. The archive entry remains available; active IDs cannot be restored twice."; }
            else { message.Text = status.Text; Load(); }
        }), true); d.Fields.Children.Add(restore);
        d.Fields.Children.Add(Btn("Permanently remove selected entry…", () => Attempt(() => { if (list.SelectedItem is ArchiveRow row && DeleteArchiveEntry(d, row.Entry.Id)) { Load(); message.Text = "Entry permanently removed."; } else message.Text = status.Text; })));
        d.Fields.Children.Add(Btn("Retry completion", () => Attempt(() => { CompleteArchiveMutation(archiveMutations.ReconcilePending()); message.Text = status.Text; Refresh(); Load(); })));
        d.Fields.Children.Add(Btn("Cancel prepared operation", () => Attempt(() => { CompleteArchiveMutation(archiveMutations.CancelPending()); message.Text = status.Text; Refresh(); Load(); })));
        d.Fields.Children.Add(Btn("Close", d.Close));
        void Load() {
            if (!ArchiveUnlocked) return;
            RefreshArchiveWarning(); pending.Text = archiveWarning.Text;
            var entries = trash ? archiveMutations.ListTrash() : selectedId is Guid id ? archiveMutations.ListHistory(id) : Array.Empty<ArchiveEntry>();
            list.ItemsSource = entries.OrderByDescending(e => e.CapturedUtc).Select(e => { var b = archiveMutations.ReadArchive(e.Id); return new ArchiveRow(e, $"{e.CapturedUtc.ToLocalTime():MMM d, yyyy HH:mm:ss} · {string.Join(", ", b.Data.Notes.Select(n => n.Title))} · {b.Data.Notes.Count} notes / {b.Data.Notebooks.Count} notebooks"); }).ToArray();
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            else { preview.Text = trash ? "Trash is empty." : "No saved versions for the selected note."; restore.IsEnabled = false; }
        }
        list.SelectionChanged += (_, _) => Attempt(() => {
            if (list.SelectedItem is not ArchiveRow row) return;
            var bundle = archiveMutations.ReadArchive(row.Entry.Id);
            preview.Text = $"Captured {row.Entry.CapturedUtc.ToLocalTime():F}\n{bundle.Data.Notes.Count} notes · {bundle.Data.Replies.Count} replies · {bundle.Library.Tasks.Count} tasks · {bundle.Library.Attachments.Count} attachments\n\n" + string.Join("\n\n", bundle.Data.Notes.Select(n => n.Title + "\n" + n.Body));
            restore.IsEnabled = !ArchivePending;
        });
        Attempt(Load); return d;
    }
    private void OpenArchive(bool trash)
    {
        if (!ArchiveUnlocked) return;
        try { BuildArchiveDialog(trash)?.ShowDialog(); }
        catch (Exception ex) when (ArchiveError(ex)) { SetStatus("Archive unavailable; original bytes preserved. " + ex.Message, true); }
    }
    private sealed record ArchiveRow(ArchiveEntry Entry, string Title);
}
