using System.IO;
using System.Windows;
using Threadbook.Core;
using Threadbook.Storage;
using Threadbook.Desktop;
internal static class FixtureProgram
{
    [STAThread] static int Main(string[] args)
    {
        try
        {
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || Directory.Exists(args[0])) { Console.Error.WriteLine("Require explicit new fixture directory."); return 1; }
        Directory.CreateDirectory(args[0]); using var store = new JsonStore(args[0]); var service = new NotebookService(store);
        var application = new Application(); var window = new MainWindow(service, store, args[0], false);
        application.DispatcherUnhandledException += (_, e) => { Console.Error.WriteLine(e.Exception); e.Handled = true; typeof(MainWindow).GetField("confirmLeaveForTest", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, new Func<bool>(() => true)); application.Shutdown(2); };
        window.ContentRendered += async (_, _) => { try { await window.RunLayoutSmokeTest(); await window.RunLibrarySmokeTest(); Console.WriteLine("PASS library WPF integration; " + args[0]); application.Shutdown(0); } catch (Exception ex) { typeof(MainWindow).GetField("confirmLeaveForTest", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, new Func<bool>(() => true)); Console.Error.WriteLine(ex); File.WriteAllText(Path.Combine(args[0], "failure.txt"), ex.ToString()); application.Shutdown(2); } };
        return application.Run(window);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
