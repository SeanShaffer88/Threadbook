using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Threadbook.Core;
using Threadbook.Storage;
using Threadbook.Desktop;

internal static class Program
{
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread]
    public static int Main(string[] args)
    {
        try {
            if (args.Length != 1 || Directory.Exists(args[0])) throw new ArgumentException("Explicit NEW fixture directory required.");
            Directory.CreateDirectory(args[0]); var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Exception? dispatcherFailure = null;
            application.DispatcherUnhandledException += (_, e) => { dispatcherFailure = e.Exception; e.Handled = true; };
            int assertions = 0;
            void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); assertions++; }
            var assembly = typeof(MainWindow).Assembly;
            var dialogsBeforeTheme = assembly.GetType("Threadbook.Desktop.PrivateDialogs")!;
            Exception? passwordFailure = null;
            application.Dispatcher.BeginInvoke(new Action(() => {
                Window? passwordWindow = null;
                try { passwordWindow = application.Windows.Cast<Window>().Single(); Check(passwordWindow.Icon is { IsFrozen: true }, "startup password icon frozen before Theme.Apply"); }
                catch (Exception ex) { passwordFailure = ex; }
                finally { passwordWindow?.Close(); }
            }));
            var passwordMethod = dialogsBeforeTheme.GetMethod("Password", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(bool));
            passwordMethod.Invoke(null, new object?[] { null, "Brand fixture", false, null, false });
            if (passwordFailure is not null) throw passwordFailure;
            object? Body(Window window) => window.Content?.GetType().GetProperty("BodyContent")?.GetValue(window.Content) ?? window.Content;
            void Initialize(MainWindow window) { Check((bool)typeof(MainWindow).GetField("privateLibraryInitialized", Hidden)!.GetValue(window)!, "private command lifecycle initialized once"); }
            using (var plain = new JsonStore(Path.Combine(args[0], "plain"))) {
                var service = new NotebookService(plain); var window = new MainWindow(service, plain, plain.Files.RootDirectory, false); Initialize(window);
                Check(window.Icon is { IsFrozen: true }, "main icon frozen");
                var brand = assembly.GetType("Threadbook.Desktop.ApplicationBrand")!;
                Check(brand.GetProperty("Mark", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is BitmapSource { IsFrozen: true, PixelWidth: 32 }, "embedded 32px header mark frozen");
                window.Show(); var libraryWindow = (Window)Activator.CreateInstance(assembly.GetType("Threadbook.Desktop.LibraryDialog")!, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { window, "Brand fixture" }, null)!;
                Check(libraryWindow.Icon is { IsFrozen: true }, "library icon frozen"); libraryWindow.Close();
                string plainExport = Path.Combine(args[0], "plain-export.json");
                var plainTicket = typeof(MainWindow).GetMethod("CapturePrivateExportTicket", Hidden)!.Invoke(window, null)!;
                Check((bool)typeof(MainWindow).GetMethod("CompletePrivateExport", Hidden)!.Invoke(window, new object[] { plainTicket, new Action(() => JsonStore.Export(service.State, plainExport, false)) })! && File.Exists(plainExport), "startup plaintext export behavior preserved");
                window.RunPrivateLibrarySmokeTest(); Check(true, "verified encrypted copy, key lock, wrong-password rejection, unlock and ciphertext backup");
                window.Close();
            }
            char[] commandPassword = "synthetic-command-fixture".ToCharArray();
            try {
                string path = Path.Combine(args[0], "command-guards");
                using var owner = JsonStore.CreateEncrypted(path, commandPassword);
                var service = new NotebookService(owner);
                Check(service.Execute(new PutNote(null, service.State.Notebooks.First().Id, "Commands", "Fixture", "")).Success, "command fixture note");
                var main = new MainWindow(service, owner, path, false);
                try {
                    main.Show();
                    var ready = new System.Windows.Threading.DispatcherFrame(); main.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => ready.Continue = false)); System.Windows.Threading.Dispatcher.PushFrame(ready);
                    typeof(MainWindow).GetField("selectedId", Hidden)!.SetValue(main, service.State.Notes.Single().Id);
                    var reply = (TextBox)typeof(MainWindow).GetField("replyBox", Hidden)!.GetValue(main)!;
                    var status = (TextBlock)typeof(MainWindow).GetField("status", Hidden)!.GetValue(main)!;
                    var save = typeof(MainWindow).GetMethod("SaveReply", Hidden)!;
                    var commands = (Dictionary<string, Action>)typeof(MainWindow).GetField("uiCommands", Hidden)!.GetValue(main)!;
                    IEnumerable<DependencyObject> Walk(DependencyObject node) {
                        yield return node;
                        var children = LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().ToList(); if (node is Visual) for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) children.Add(VisualTreeHelper.GetChild(node, i)); foreach (var child in children.Distinct()) foreach (var descendant in Walk(child)) yield return descendant;
                    }
                    var retained = Walk(main).Distinct().OfType<System.Windows.Controls.Primitives.ButtonBase>().Where(b => b.Command == ApplicationCommands.Undo || b.Command == ApplicationCommands.Redo).Select(b => ((RoutedCommand)b.Command!, b.CommandTarget!)).Concat(
                        Walk(main).Distinct().OfType<MenuItem>().Where(b => b.Command == ApplicationCommands.Undo || b.Command == ApplicationCommands.Redo).Select(b => ((RoutedCommand)b.Command!, b.CommandTarget!))).ToArray();
                    Check(retained.Length == 4 && retained.All(c => ReferenceEquals(c.Item2, reply)), "retain actual undo redo menu and toolbar targets");
                    reply.Clear(); reply.IsUndoEnabled = false; reply.IsUndoEnabled = true; reply.AppendText("first"); reply.AppendText(" second");
                    string draft = reply.Text;
                    ApplicationCommands.Undo.Execute(null, reply); Check(reply.Text != draft && reply.CanRedo, "normal undo preserves textbox semantics");
                    ApplicationCommands.Redo.Execute(null, reply); Check(reply.Text == draft && reply.CanUndo, "normal redo preserves textbox semantics");
                    reply.Select(1, 2); int caret = reply.CaretIndex;
                    Check((bool)typeof(MainWindow).GetMethod("TryLockVault", Hidden)!.Invoke(main, null)!, "command fixture encrypted lock");
                    long revision = service.State.Revision;
                    string[] Hashes() => Directory.GetFiles(path, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != ".threadbook.lock").Order().Select(f => f + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))).ToArray();
                    var hashes = Hashes(); var priorStatus = status.Text;
                    foreach (var (command, target) in retained) {
                        Check(!command.CanExecute(null, target), "locked retained command unavailable: " + command.Name);
                        command.Execute(null, target); Check(reply.Text == draft, "retained execute unchanged " + command.Name);

                    }
                    save.Invoke(main, null); commands["Save reply"]();
                    main.Dispatcher.BeginInvoke(new Action(() => { save.Invoke(main, null); commands["Save reply"](); }));
                    var pump = new System.Windows.Threading.DispatcherFrame();
                    main.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => pump.Continue = false));
                    System.Windows.Threading.Dispatcher.PushFrame(pump);
                    if (dispatcherFailure is not null) throw dispatcherFailure;
                    Check(reply.Text == draft && reply.CaretIndex == caret && reply.SelectionLength == 2, "locked forced commands and queued save preserve draft selection: text=" + (reply.Text == draft) + " caret=" + reply.CaretIndex + "/" + caret + " selection=" + reply.SelectionLength);
                    Check(service.State.Revision == revision && Hashes().SequenceEqual(hashes) && status.Text == priorStatus, "locked commands leave canonical bytes revision and status unchanged");
                    Check(!((Button)typeof(MainWindow).GetField("saveReplyCommand", Hidden)!.GetValue(main)!).IsEnabled, "locked save disabled");
                    owner.Unlock(commandPassword);
                    int writes = 0; owner.BeforeReplace = () => writes++;
                    save.Invoke(main, null); commands["Save reply"]();
                    main.Dispatcher.BeginInvoke(new Action(() => { save.Invoke(main, null); commands["Save reply"](); })); var spyPump = new System.Windows.Threading.DispatcherFrame(); main.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => spyPump.Continue = false)); System.Windows.Threading.Dispatcher.PushFrame(spyPump); if (dispatcherFailure is not null) throw dispatcherFailure;
                    Check(writes == 0 && service.State.Revision == revision && reply.Text == draft && status.Text == priorStatus, "save entry rejects locked UI even with live storage key and write spy");
                    owner.BeforeReplace = null;
                    typeof(MainWindow).GetMethod("ResumePrivateVault", Hidden)!.Invoke(main, null);
                    Check(((Button)typeof(MainWindow).GetField("saveReplyCommand", Hidden)!.GetValue(main)!).IsEnabled, "unlock refreshes save availability");
                    ApplicationCommands.Undo.Execute(null, reply); Check(reply.Text != draft, "unlock undo history retained");
                    ApplicationCommands.Redo.Execute(null, reply); Check(reply.Text == draft, "unlock redo history retained");
                    commands["Save reply"](); Check(service.State.Revision > revision && service.State.Replies.Single().Body == draft && reply.Text.Length == 0, "unlocked registered save persists normally");
                } finally {
                    foreach (var owned in main.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
                    typeof(MainWindow).GetField("vaultLocked", Hidden)!.SetValue(main, true); main.Close();
                }
            } finally { Array.Clear(commandPassword); }
            char[] password = "synthetic-ui-fixture-password".ToCharArray();
            try {
                string encryptedPath = Path.Combine(args[0], "encrypted");
                using var owner = JsonStore.CreateEncrypted(encryptedPath, password); var service = new NotebookService(owner);
                var book = service.State.Notebooks.First(); Check(service.Execute(new PutNote(null, book.Id, "Fixture", "Private image", "")).Success, "encrypted canonical fixture");
                var note = service.State.Notes.Single(); var library = new LibraryService(new LibraryStore(owner), () => service.State); var attachments = new AttachmentStore(owner, library);
                using var imageStream = new MemoryStream();
                var pixels = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(pixels)); encoder.Save(imageStream); imageStream.Position = 0;
                var stage = attachments.Stage(note.Id, imageStream, "fixture.png", "image/png"); Check(attachments.AddToNote(stage).Success, "image attached to committed note");
                using (var bad = new MemoryStream(new byte[] { 1, 2, 3 })) { var abandoned = attachments.Stage(Guid.NewGuid(), bad, "fixture.bin", "application/octet-stream"); Check(!attachments.AddToNote(abandoned).Success && owner.Files.List("attachments/").Count == 1, "failed metadata removes staged blob"); }
                var dialogs = typeof(MainWindow).Assembly.GetType("Threadbook.Desktop.PrivateDialogs")!;
                using (var read = attachments.OpenRead(stage.Info.Id, note.Id)) { var bitmap = (BitmapSource)dialogs.GetMethod("DecodeImage", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { read })!; Check(bitmap.IsFrozen && bitmap.PixelWidth == 2, "bounded in-memory PNG preview"); }
                bool rejected = false; try { using var invalid = new MemoryStream(new byte[64]); dialogs.GetMethod("DecodeImage", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { invalid }); } catch (TargetInvocationException) { rejected = true; } Check(rejected, "malformed image rejected");
                var main = new MainWindow(service, owner, encryptedPath, false); Initialize(main); main.Show();
                typeof(MainWindow).GetField("selectedId", Hidden)!.SetValue(main, note.Id);
                Exception? attachmentFailure = null;
                main.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => {
                    Window? dialog = null;
                    try {
                        dialog = main.OwnedWindows.Cast<Window>().Single(w => w.Title == "Attachments · saved note");
                        Check(dialog.Icon is { IsFrozen: true }, "attachment icon frozen");
                        dialog.Width = 200; dialog.Height = 100; dialog.UpdateLayout();
                        Check(dialog.MinWidth > 200 && dialog.MinWidth <= 600 && dialog.MinHeight > 100 && dialog.MinHeight <= 480 && dialog.ActualWidth >= dialog.MinWidth && dialog.ActualHeight >= dialog.MinHeight, "attachments resize coerced to monitor-safe minimum");
                        var panel = (DockPanel)Body(dialog)!; var controls = (WrapPanel)panel.Children[0];
                        foreach (Button button in controls.Children) {
                            var bounds = button.TransformToAncestor(panel).TransformBounds(new Rect(button.RenderSize));
                            Check(button.IsVisible && bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= panel.ActualWidth && bounds.Bottom <= panel.ActualHeight, "attachment button visible within minimum content bounds: " + button.Content);
                        }
                    } catch (Exception ex) { attachmentFailure = ex; } finally { dialog?.Close(); }
                }));
                typeof(MainWindow).GetMethod("OpenAttachments", Hidden)!.Invoke(main, null);
                if (attachmentFailure is not null) throw attachmentFailure;
                var editorType = typeof(MainWindow).Assembly.GetType("Threadbook.Desktop.NoteEditor")!;
                var editor = (Window)editorType.GetConstructors().Single(c => c.GetParameters().Length == 9).Invoke(new object?[] { main, service.State.Notebooks, note, book.Id, new Func<Command, bool>(c => service.Execute(c).Success), new RecoveryStore(owner), service.State.Revision, encryptedPath, null });
                Check(editor.Icon is { IsFrozen: true }, "editor icon frozen");
                ((TextBox)editorType.GetField("body", Hidden)!.GetValue(editor)!).Text = "Unsaved encrypted recovery fixture";
                editor.Show();
                var content = Body(main);
                var capture = typeof(MainWindow).GetMethod("CapturePrivateExportTicket", Hidden)!;
                var complete = typeof(MainWindow).GetMethod("CompletePrivateExport", Hidden)!;
                var ticket = capture.Invoke(main, null)!;
                string exportPath = Path.Combine(args[0], "continued-export.md");
                bool Complete(object captured) => (bool)complete.Invoke(main, new object[] { captured, new Action(() => File.WriteAllText(exportPath, "synthetic export")) })!;
                string blockedDraft = Path.Combine(encryptedPath, "recovery-draft.json"); Directory.CreateDirectory(blockedDraft);
                Check(!(bool)typeof(MainWindow).GetMethod("TryLockVault", Hidden)!.Invoke(main, null)! && owner.Files.IsUnlocked && editor.IsVisible && ReferenceEquals(content, Body(main)), "draft write failure retains unlocked UI and key honestly");
                Directory.Delete(blockedDraft);
                Check((bool)typeof(MainWindow).GetMethod("TryLockVault", Hidden)!.Invoke(main, null)!, "manual lock succeeds");
                Check(!owner.Files.IsUnlocked && !ReferenceEquals(content, Body(main)), "key wiped and content hidden");
                                Check(!editor.IsVisible, "owned editor hidden while locked");
                var privateFrame = main.Content!; Check((bool)privateFrame.GetType().GetProperty("IsPrivacyLocked")!.GetValue(privateFrame)!, "caption privacy locked");
                var searchControl = (TextBox)typeof(MainWindow).GetField("search", Hidden)!.GetValue(main)!;
                Check(!searchControl.IsVisible && PresentationSource.FromVisual(searchControl) is null, "private search detached from active presentation");
                Check(!Complete(ticket) && !File.Exists(exportPath), "pending export writes nothing while locked");
                rejected = false; try { owner.Unlock("incorrect-fixture-password".AsSpan()); } catch (DataException) { rejected = true; } Check(rejected && !owner.Files.IsUnlocked, "wrong password retains locked state");
                owner.Unlock(password); typeof(MainWindow).GetMethod("ResumePrivateVault", Hidden)!.Invoke(main, null); Check(ReferenceEquals(content, Body(main)), "unlock restores original view");
                Check(!Complete(ticket) && !File.Exists(exportPath), "lock then unlock invalidates original export ticket");
                Check(Complete(capture.Invoke(main, null)!) && File.Exists(exportPath), "fresh unlocked export ticket writes successfully");
                Check(new RecoveryStore(owner).LoadDraft()?.Body == "Unsaved encrypted recovery fixture", "unsaved editor draft flushed before key wipe");
                // Pump the queued modal resume and close it inside its nested loop.
                main.Dispatcher.BeginInvoke(new Action(() => editor.Close()));
                var frame = new System.Windows.Threading.DispatcherFrame(); main.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => frame.Continue = false)); System.Windows.Threading.Dispatcher.PushFrame(frame);
                Check(!editor.IsVisible, "resumed modal editor closes with recovery retained");
                typeof(MainWindow).GetField("lastPrivateInput", Hidden)!.SetValue(main, System.Diagnostics.Stopwatch.GetTimestamp() - 61 * System.Diagnostics.Stopwatch.Frequency);
                typeof(MainWindow).GetField("idleMinutes", Hidden)!.SetValue(main, 0);
                Check(!(bool)typeof(MainWindow).GetMethod("CheckPrivateIdle", Hidden)!.Invoke(main, null)! && owner.Files.IsUnlocked, "auto-lock Off respects setting");
                typeof(MainWindow).GetField("idleMinutes", Hidden)!.SetValue(main, 1);
                Check((bool)typeof(MainWindow).GetMethod("CheckPrivateIdle", Hidden)!.Invoke(main, null)! && !owner.Files.IsUnlocked, "idle deadline locks encrypted vault");
                owner.Unlock(password); typeof(MainWindow).GetMethod("ResumePrivateVault", Hidden)!.Invoke(main, null);
                string backup = Path.Combine(args[0], "ciphertext.zip");
                bool CompleteBackup(object captured) => (bool)complete.Invoke(main, new object[] { captured, new Action(() => typeof(MainWindow).GetMethod("WritePrivateBackup", Hidden)!.Invoke(main, new object[] { backup })) })!;
                Check(!CompleteBackup(ticket) && !File.Exists(backup), "backup rejects changed lock epoch after unlock without writes");
                Check(CompleteBackup(capture.Invoke(main, null)!) && File.Exists(backup), "ordinary guarded ciphertext backup works");
                string restored = Path.Combine(args[0], "restored"); using (var read = File.OpenRead(backup)) PortableLibrary.Restore(read, restored, new string(password));
                using (var copy = JsonStore.OpenEncrypted(restored, password)) Check(copy.Load().Notes.Single().Body == "Private image", "encrypted portable backup restored to new directory");
                var preferencePath = new LayoutPreferenceStore(owner).PreferencePath;
                var preferenceBytes = File.Exists(preferencePath) ? File.ReadAllBytes(preferencePath) : null;
                Check((bool)typeof(MainWindow).GetMethod("TryLockVault", Hidden)!.Invoke(main, null)!, "lock before closing fixture");
                main.Close();
                Check(preferenceBytes is null ? !File.Exists(preferencePath) : File.ReadAllBytes(preferencePath).SequenceEqual(preferenceBytes), "closing while locked preserves preference bytes");
            } finally { Array.Clear(password); }
            if (dispatcherFailure is not null) throw dispatcherFailure;
            Console.WriteLine($"PASS {assertions} private UI fixture assertions"); application.Shutdown(); return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 2; }
    }
}
