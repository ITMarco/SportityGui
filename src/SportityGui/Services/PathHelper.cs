using System.IO;

namespace SportityGui.Services;

internal static class PathHelper
{
    public static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
    }

    public static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static bool SameFolder(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// True when <paramref name="path"/> is inside (or equal to) <paramref name="root"/>.
    public static bool IsUnder(string path, string root)
    {
        try
        {
            var p = Normalize(path);
            var r = Normalize(root);
            return p.Equals(r, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// Walks up from <paramref name="folder"/> to the nearest directory that exists (for dialog start locations).
    public static string? NearestExisting(string? folder)
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(folder);
            while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                dir = Path.GetDirectoryName(dir);
            return dir;
        }
        catch { return null; }
    }

    /// Creates the folder if needed and checks that a file can be written to it.
    public static bool CanWrite(string folder, out string error)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".sportitygui-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// Returns <paramref name="path"/>, or "name (2).ext" etc. if a file already exists there.
    public static string UniqueFilePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
