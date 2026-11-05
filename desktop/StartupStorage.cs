using System.IO;

namespace Threadbook.Desktop;

// Desktop-only storage selection. No writes, migration, or fallback on errors.
internal static class StartupStorage
{
    internal static string Resolve(string? explicitDirectory, string applicationDirectory, string defaultDirectory)
    {
        if (explicitDirectory is not null) return Path.GetFullPath(explicitDirectory);
        string marker = Path.Combine(applicationDirectory, "portable.flag");
        // GetAttributes fails visibly on inaccessible markers instead of selecting a default.
        FileAttributes attributes;
        try { attributes = File.GetAttributes(marker); }
        catch (FileNotFoundException) { return defaultDirectory; }
        catch (DirectoryNotFoundException) { return defaultDirectory; }
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("portable.flag must be an ordinary file.");
        string portable = Path.GetFullPath(Path.Combine(applicationDirectory, "Data"));
        for (string? path = portable; path is not null; path = Path.GetDirectoryName(path))
        {
            try {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Portable storage cannot use filesystem links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return portable;
    }
}
