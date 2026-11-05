using System.IO;
using System.Text;
namespace Threadbook.Desktop;
// Reject reparse points anywhere in either path rather than resolving mutable aliases.
public static class ExportSafety
{
    public static bool IsProtected(string destination, string liveDirectory)
    {
        string full = Path.GetFullPath(destination), root = Path.GetFullPath(liveDirectory).TrimEnd(Path.DirectorySeparatorChar);
        bool HasAlias(string p) { for (string? current = p; current is not null; current = Path.GetDirectoryName(current)) { if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true; } return false; }
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || HasAlias(full) || HasAlias(root);
    }
    public static void Write(string destination, string liveDirectory, string text)
    {
        if (IsProtected(destination, liveDirectory)) throw new IOException("Choose a destination outside the live data folder and without filesystem links.");
        string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, ".threadbook-export-" + Guid.NewGuid() + ".tmp");
        try { using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { var bytes = new UTF8Encoding(false).GetBytes(text); stream.Write(bytes); stream.Flush(true); }
            if (IsProtected(destination, liveDirectory)) throw new IOException("Export destination changed.");
            if (File.Exists(destination)) File.Replace(temp, destination, null); else File.Move(temp, destination);
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void CopyPreserved(string source, string destination, string liveDirectory)
    {
        if(IsProtected(destination, liveDirectory)) throw new IOException("Choose a destination outside the live data folder without filesystem links.");
        string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, ".threadbook-preserved-" + Guid.NewGuid() + ".tmp");
        try { using(var input = new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read)) using(var output = new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { input.CopyTo(output); output.Flush(true); }
            if(IsProtected(destination,liveDirectory)) throw new IOException("Export destination changed.");
            if(File.Exists(destination)) File.Replace(temp,destination,null); else File.Move(temp,destination);
        } finally { if(File.Exists(temp)) File.Delete(temp); }
    }
}
