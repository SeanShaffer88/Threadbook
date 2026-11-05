using System.IO;
using System.Windows;
using Microsoft.Win32;
using Threadbook.Core;
using Threadbook.Storage;

namespace Threadbook.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        JsonStore? store = null;
        try
        {
            var startup = System.Diagnostics.Stopwatch.StartNew();
            string? dataDir = null, screenshot = null; bool demo = false, smoke = false, benchmark = false, prepareBenchmark = false, layoutSmoke = false, expansionSmoke = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--data-dir" when i + 1 < args.Length: dataDir = Path.GetFullPath(args[++i]); break;
                    case "--screenshot" when i + 1 < args.Length: screenshot = Path.GetFullPath(args[++i]); break;
                    case "--demo": demo = true; break;
                    case "--smoke-test": smoke = true; break;
                    case "--layout-smoke-test": layoutSmoke = true; smoke = true; break;
                    case "--expansion-smoke-test": expansionSmoke = true; smoke = true; break;
                    case "--benchmark": benchmark = true; break;
                    case "--prepare-benchmark": prepareBenchmark = true; benchmark = true; break;
                    default: throw new ArgumentException("Usage: Threadbook [--data-dir folder] [--demo] [--screenshot file.png] [--smoke-test | --layout-smoke-test]");
                }
            }
            if ((demo || smoke || benchmark || screenshot is not null) && dataDir is null) throw new ArgumentException("Test/demo options require an explicit --data-dir fixture folder.");
            if (smoke && File.Exists(Path.Combine(dataDir!, "threadbook.json"))) throw new ArgumentException("Smoke test requires a new fixture folder.");
            dataDir = StartupStorage.Resolve(dataDir, AppContext.BaseDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Threadbook"));
            bool fresh = !File.Exists(Path.Combine(dataDir, "threadbook.json"));
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (JsonStore.IsEncryptedDirectory(dataDir)) {
                if (smoke || demo || benchmark || screenshot is not null) throw new ArgumentException("Automated modes require a new plaintext synthetic fixture directory.");
                if (!PrivateDialogs.Password(null, "Unlock Threadbook vault", false, password => JsonStore.OpenEncrypted(dataDir, password), out JsonStore unlocked)) return 0;
                store = unlocked;
            } else store = new JsonStore(dataDir);
            NotebookService service;
            try { service = new NotebookService(store); }
            catch (DataException ex) when (ex.Code is ErrorCode.CorruptData or ErrorCode.Validation or ErrorCode.CapacityExceeded or ErrorCode.UnsupportedVersion)
            {
                if (smoke || screenshot is not null) throw;
                if (store.Files.IsEncrypted) { MessageBox.Show("Encrypted vault data could not be loaded. Original encrypted bytes are unchanged. Use a validated portable backup restored to a new directory.", "Threadbook — encrypted recovery", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
                var message = ex.Message + "\n\nYour file is unchanged. Yes: export the damaged file. No: choose a backup to recover. Cancel: exit.";
                var choice = MessageBox.Show(message, "Threadbook — recovery", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (choice == MessageBoxResult.Yes)
                {
                    var save = new SaveFileDialog { Filter = "JSON file|*.json", FileName = "Threadbook-preserved.json" };
                    if (save.ShowDialog() == true) { if(ExportSafety.IsProtected(save.FileName, dataDir)) MessageBox.Show("Choose a destination outside the live data directory without filesystem links. Original bytes unchanged."); else ExportSafety.CopyPreserved(store.PrimaryPath, save.FileName, dataDir); }
                    return 1;
                }
                if (choice != MessageBoxResult.No || ex.Code == ErrorCode.UnsupportedVersion) return 1;
                var open = new OpenFileDialog { InitialDirectory = dataDir, Filter = "Threadbook backup|backup-*.json|JSON file|*.json" };
                if (open.ShowDialog() != true || MessageBox.Show("Replace the damaged file with this validated backup? The damaged bytes will be preserved in the data folder.", "Recover backup", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return 1;
                store.Recover(open.FileName); service = new NotebookService(store);
            }
            if (demo && fresh) Demo.Seed(service);
            if(benchmark && fresh) BenchmarkFixture.Seed(service);
            if(prepareBenchmark) return 0;
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var window = new MainWindow(service, store, dataDir, !smoke && !benchmark && screenshot is null);
            app.MainWindow = window;
            if (screenshot is not null || smoke || benchmark)
            {
                window.ContentRendered += async (_, _) =>
                {
                    try
                    {
                        double readyMs = startup.Elapsed.TotalMilliseconds;
                        if(benchmark) await window.RunBenchmark(readyMs);
                        if (smoke) { if (expansionSmoke) { await window.RunLibrarySmokeTest(); window.RunPrivateLibrarySmokeTest(); } else if (layoutSmoke) await window.RunLayoutSmokeTest(); else await window.RunSmokeTest(); }
                        await System.Threading.Tasks.Task.Delay(350);
                        if (screenshot is not null) window.Capture(screenshot);
                        if (smoke) File.WriteAllText(Path.Combine(dataDir, "smoke-result.txt"), expansionSmoke ? "PASS: library expansion handlers and private vault copy/lock/unlock/backup smoke. See library-ui-evidence.txt and private fixture evidence." : layoutSmoke ? "PASS: six real WPF layouts, draft/target/collapse preservation, canonical hash, board placement, tab contexts, preferences, minimum bounds and screenshots. See layout-evidence.txt." : "PASS: real WPF handlers; note/reply edits; nested replies; collapse; search; saved; failed-save draft retention; export/import; deletions; themes.");
                        app.Shutdown(0);
                    }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(dataDir, "smoke-result.txt"), ex.ToString()); app.Shutdown(2); }
                };
            }
            return app.Run(window);
        }
        catch (Exception ex) when (ex is DataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (args.Contains("--smoke-test") || args.Contains("--layout-smoke-test") || args.Contains("--expansion-smoke-test") || args.Contains("--screenshot") || args.Contains("--benchmark") || args.Contains("--prepare-benchmark")) { Console.Error.WriteLine(ex.Message); return 1; }
            // GUI errors are local and contain no notebook content.
            MessageBox.Show(ex.Message, "Threadbook", MessageBoxButton.OK, MessageBoxImage.Error); return 1;
        }
        finally { store?.Dispose(); }
    }
}

internal static class Demo
{
    public static void Seed(NotebookService app)
    {
        var book = app.State.Notebooks.First();
        void Run(Command c) { var r = app.Execute(c); if (!r.Success) throw new DataException(r.Error!.Code, r.Error.Message); }
        Run(new PutNote(null, book.Id, "Small tools I keep coming back to", "•  Fast capture\n•  Plain text export\n•  Works offline", "Reference"));
        var reference = app.State.Notes.Last().Id;
        Run(new PutReply(null, reference, null, "The best tools make it easy to start."));
        Run(new PutReply(null, reference, null, "Keep the important things close."));
        Run(new PutNote(null, book.Id, "What makes an idea worth building?", "Is it the usefulness, the novelty, or just how often I keep coming back to it? Trying to find a simple way to evaluate ideas before I invest more time.", "Question"));
        var question = app.State.Notes.Last().Id;
        Run(new PutReply(null, question, null, "Start with the problem you keep noticing."));
        Run(new PutNote(null, book.Id, "A notebook that feels like Reddit", "Topics for organization. Posts for notes. Threads for thinking things through.", "Product idea"));
        var note = app.State.Notes.Last().Id; Run(new ToggleSaved(note));
        Run(new PutReply(null, note, null, "The key is being able to think out loud beneath a note.")); var root = app.State.Replies.Last().Id;
        Run(new PutReply(null, note, root, "Each reply can explore one branch without cluttering the original.")); var child = app.State.Replies.Last().Id;
        Run(new PutReply(null, note, child, "And I can collapse a branch once I have made a decision."));
        Run(new PutReply(null, note, null, "Keep the familiar layout, but make everything private.")); root = app.State.Replies.Last().Id;
        Run(new PutReply(null, note, root, "No follower counts. Just notebooks, notes, and my own thinking."));
    }
}
