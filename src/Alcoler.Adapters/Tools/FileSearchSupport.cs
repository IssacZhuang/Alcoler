using System.IO.Enumeration;

namespace Alcoler.Adapters.Tools;

// Shared by GrepTool/GlobTool: hidden files are searched, but version-control
// directories and reparse points must never be traversed.
internal static class FileSearchSupport
{
    private static readonly string[] VcsDirectoryNames = [".git", ".svn", ".hg", ".bzr", ".jj", ".sl"];

    public static IEnumerable<string> EnumerateFiles(string root) =>
        new FileSystemEnumerable<string>(
            root,
            (ref FileSystemEntry entry) => entry.ToSpecifiedFullPath(),
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory || !IsVcsDirectory(ref entry),
        };

    public static DateTime GetLastWriteTimeUtcSafe(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    public static int CompareNewestFirst((DateTime MtimeUtc, string Path) left, (DateTime MtimeUtc, string Path) right)
    {
        var byTime = right.MtimeUtc.CompareTo(left.MtimeUtc);
        return byTime != 0 ? byTime : string.CompareOrdinal(left.Path, right.Path);
    }

    public static string ToDisplayPath(string root, string filePath)
    {
        var relative = Path.GetRelativePath(root, filePath);
        if (relative.Length > 0 && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            return relative.Replace('\\', '/');
        return filePath;
    }

    private static bool IsVcsDirectory(ref FileSystemEntry entry)
    {
        foreach (var name in VcsDirectoryNames)
        {
            if (entry.FileName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
