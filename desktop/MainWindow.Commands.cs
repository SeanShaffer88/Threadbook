using System.Windows;
using System.Windows.Input;

namespace Threadbook.Desktop;

public sealed partial class MainWindow
{
    private CommandRegistry commandRegistry = null!;
    private CommandPalette? commandPalette;
    private static string CommandId(string label) => label switch {
        "New note" => "note.new", "Export" => "vault.export", "Import" => "vault.import",
        "Import files…" => "notes.import-files", "Import folder…" => "notes.import-folder",
        "Encrypted copy…" => "vault.encrypted-copy", "Attachments…" => "note.attachments",
        "Backup…" => "vault.backup", "Restore backup…" => "vault.restore-backup", "Open vault…" => "vault.open",
        "Save reply" => "reply.save", "Command palette" => "commands.open", "History" => "archive.history", "Trash" => "archive.trash",
        "Search" => "search.focus", "Search filters" => "search.filters", "Theme: Dark / Light / System" => "appearance.theme-cycle",
        "Due / overdue" => "search.overdue", "Manage notebooks" => "notebooks.manage", "Library" => "library.manage", "Templates" => "templates.manage",
        "Lock vault" => "vault.lock", "Auto-lock: Off" => "vault.autolock.off", "Auto-lock: 5 minutes" => "vault.autolock.5",
        "Auto-lock: 15 minutes" => "vault.autolock.15", "Auto-lock: 30 minutes" => "vault.autolock.30",
        _ => throw new ArgumentException("Command requires an explicit stable ID.", nameof(label))
    };
    private string? CommandPrivacy() => vaultLocked || !store.Files.IsUnlocked ? "Vault is locked." : null;
    private string? CommandAvailability(string label)
    {
        if (CommandPrivacy() is string privacy) return privacy;
        try {
            if (label == "Lock vault" && !store.Files.IsEncrypted) return "This library is not encrypted.";
            if (label == "Save reply") {
                if (!app.State.Notes.Any(n => n.Id == selectedId)) return "Choose a note first.";
                if (string.IsNullOrWhiteSpace(replyBox.Text)) return "Write a reply first.";
                if (editingReplyId is Guid id && app.State.Replies.FirstOrDefault(r => r.Id == id)?.Body == replyBox.Text) return "Reply has no changes.";
            }
            if (label == "Attachments…" && !app.State.Notes.Any(n => n.Id == selectedId)) return "Choose a note first.";
            if (label is "History" or "Trash" or "Search" or "Search filters" or "Theme: Dark / Light / System" or "Lock vault" or "Command palette" || label.StartsWith("Auto-lock:", StringComparison.Ordinal)) return null;
            if (ArchivePending) return "Complete the pending operation in History or Trash first.";
            if (library.WritesBlocked) return "Library metadata is unavailable.";
            return null;
        } catch (Exception ex) when (ArchiveError(ex)) { return "Library is unavailable."; }
    }
    private IEnumerable<PaletteCommand> PaletteEntries()
    {
        if (CommandPrivacy() is not null) yield break;
        foreach (var command in commandRegistry.Entries) yield return command;
        foreach (var book in app.State.Notebooks.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Id)) {
            Guid id = book.Id;
            yield return new("notebook:" + id, book.Name, "Notebooks", () => app.State.Notebooks.Any(b => b.Id == id) ? null : "Notebook no longer exists.", () => TryNavigate(id, null, reveal: true));
        }
        foreach (var note in app.State.Notes.OrderBy(n => n.Title, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Id)) {
            Guid id = note.Id;
            yield return new("note:" + id, note.Title, "Notes", () => app.State.Notes.Any(n => n.Id == id) ? null : "Note no longer exists.", () => TryNavigate(null, id, reveal: true));
        }
    }
    private void OpenCommandPalette()
    {
        if (CommandPrivacy() is not null) return;
        if (commandPalette is not null) { commandPalette.Activate(); commandPalette.FocusQuery(); return; }
        var previous = Keyboard.FocusedElement;
        var palette = new CommandPalette(this, commandRegistry, PaletteEntries);
        commandPalette = palette;
        palette.Closed += (_, _) => {
            commandPalette = null;
            if (CommandPrivacy() is null && IsVisible && previous is UIElement element && element.IsVisible && element.IsEnabled) element.Focus();
        };
        palette.Show();
    }
}
