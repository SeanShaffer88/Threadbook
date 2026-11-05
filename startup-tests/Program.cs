using Threadbook.Desktop;
using Threadbook.Storage;

if (args.Length != 1) throw new ArgumentException("Supply a new synthetic fixture directory.");
string root = Path.GetFullPath(args[0]);
if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Fixture must be new.");
Directory.CreateDirectory(root);
string app = Path.Combine(root, "app"), fallback = Path.Combine(root, "unused-default"), explicitDir = Path.Combine(root, "explicit");
Directory.CreateDirectory(app);
void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS: " + message); }
Check(StartupStorage.Resolve(null, app, fallback) == fallback, "unchanged default selection without accessing it");
File.WriteAllText(Path.Combine(app, "portable.flag"), "");
string foreign = Path.Combine(root, "foreign"); Directory.CreateDirectory(foreign);
Environment.CurrentDirectory = foreign;
Check(StartupStorage.Resolve(null, app, fallback) == Path.Combine(app, "Data"), "marker uses application directory from foreign CWD");
Check(StartupStorage.Resolve(explicitDir, app, fallback) == explicitDir, "explicit path wins over marker");
using (var store = new JsonStore(StartupStorage.Resolve(null, app, fallback))) store.Load();
Check(File.Exists(Path.Combine(app, "Data", "threadbook.json")), "real storage writes selected portable fixture");
Check(!Directory.Exists(fallback), "default directory never accessed or initialized");
// Actual FileStream lock prevents writes; this is not a simulated permission check.
string lockedApp = Path.Combine(root, "locked-app"); Directory.CreateDirectory(lockedApp);
File.WriteAllText(Path.Combine(lockedApp, "portable.flag"), "");
Directory.CreateDirectory(Path.Combine(lockedApp, "Data"));
using (var lease = new FileStream(Path.Combine(lockedApp, "Data", ".threadbook.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) {
    bool rejected = false;
    try { using var store = new JsonStore(StartupStorage.Resolve(null, lockedApp, fallback)); }
    catch (Exception ex) when (ex is IOException or Threadbook.Core.DataException) { rejected = true; }
    Check(rejected && !Directory.Exists(fallback), "unwritable locked portable storage fails without fallback");
}
Console.WriteLine("Read-only ACL test: not performed; exclusive-lock failure is distinct from permission denial.");
string badMarkerApp = Path.Combine(root, "bad-marker"); Directory.CreateDirectory(badMarkerApp);
Directory.CreateDirectory(Path.Combine(badMarkerApp, "portable.flag"));
bool badMarkerRejected = false;
try { StartupStorage.Resolve(null, badMarkerApp, fallback); } catch (IOException) { badMarkerRejected = true; }
Check(badMarkerRejected, "directory marker is rejected without fallback");
Check(StartupStorage.Resolve(explicitDir, badMarkerApp, fallback) == explicitDir, "explicit override preserves semantics despite invalid marker");
if (OperatingSystem.IsWindows()) {
    void Junction(string link, string target) {
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException($"Junction creation exit {process.ExitCode}: {output} {error}");
    }
    foreach (bool ancestor in new[] { false, true }) {
        string target = Path.Combine(root, ancestor ? "ancestor-target" : "data-target"); Directory.CreateDirectory(target);
        string linkedApp = Path.Combine(root, ancestor ? "ancestor-app" : "data-app");
        string link = ancestor ? linkedApp : Path.Combine(linkedApp, "Data");
        if (!ancestor) Directory.CreateDirectory(linkedApp);
        Junction(link, target);
        File.WriteAllText(Path.Combine(linkedApp, "portable.flag"), "");
        bool rejected = false;
        try { StartupStorage.Resolve(null, linkedApp, fallback); } catch (IOException) { rejected = true; }
        Check(rejected && !Directory.Exists(fallback) && !File.Exists(Path.Combine(target, "threadbook.json")), ancestor ? "application ancestor junction rejected without initialization" : "Data junction rejected without initialization");
    }
    string markerApp = Path.Combine(root, "symlink-marker-app"); Directory.CreateDirectory(markerApp);
    string markerTarget = Path.Combine(root, "marker-target"); File.WriteAllText(markerTarget, "");
    bool markerLinkSupported = true;
    try { File.CreateSymbolicLink(Path.Combine(markerApp, "portable.flag"), markerTarget); }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) {
        markerLinkSupported = false;
        Console.WriteLine("UNSUPPORTED: marker symbolic-link fixture creation: " + ex.Message);
    }
    if (markerLinkSupported) {
        bool rejected = false;
        try { StartupStorage.Resolve(null, markerApp, fallback); } catch (IOException) { rejected = true; }
        Check(rejected && !Directory.Exists(fallback) && !Directory.Exists(Path.Combine(markerApp, "Data")), "marker symbolic link rejected without initialization");
    }
    string readOnlyApp = Path.Combine(root, "read-only-app"); Directory.CreateDirectory(readOnlyApp);
    File.WriteAllText(Path.Combine(readOnlyApp, "portable.flag"), "");
    string readOnlyData = Path.Combine(readOnlyApp, "Data"); Directory.CreateDirectory(readOnlyData);
    string readOnlyLock = Path.Combine(readOnlyData, ".threadbook.lock"); File.WriteAllText(readOnlyLock, "");
    File.SetAttributes(readOnlyLock, FileAttributes.ReadOnly);
    try {
        bool actualWriteDenied = false;
        try { using var probe = new FileStream(readOnlyLock, FileMode.Open, FileAccess.ReadWrite); }
        catch (UnauthorizedAccessException) { actualWriteDenied = true; }
        Check(actualWriteDenied, "OS enforces actual read-only portable lock file");
        bool storageDenied = false;
        try { using var store = new JsonStore(StartupStorage.Resolve(null, readOnlyApp, fallback)); }
        catch (UnauthorizedAccessException) { storageDenied = true; }
        Check(storageDenied && !Directory.Exists(fallback), "actual read-only portable storage fails without default fallback");
    } finally { File.SetAttributes(readOnlyLock, FileAttributes.Normal); }
}
