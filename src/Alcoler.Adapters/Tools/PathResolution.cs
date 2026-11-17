namespace Alcoler.Adapters.Tools;

public static class PathResolution
{
    private const int MaxSymbolicLinks = 40;

    public static string ResolveSymbolicLinks(string path)
    {
        // Windows normalization must not require filesystem access.
        if (OperatingSystem.IsWindows())
            return Path.GetFullPath(path);
        ArgumentException.ThrowIfNullOrEmpty(path);
        // Unix parent traversal must follow symlink expansion.
        var full = Path.IsPathRooted(path) ? path : Path.Join(Environment.CurrentDirectory, path);

        var current = "/";
        var pending = new Stack<string>(full.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse());
        var linksFollowed = 0;
        while (pending.TryPop(out var segment))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                current = Path.GetDirectoryName(current) ?? "/";
                continue;
            }

            var next = Path.Combine(current, segment);
            var attributes = File.GetAttributes(next);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                if (pending.Count > 0 && (attributes & FileAttributes.Directory) == 0)
                    throw new IOException($"Path component '{next}' is not a directory.");
                current = next;
                continue;
            }

            if (++linksFollowed > MaxSymbolicLinks)
                throw new IOException($"Too many symbolic links in path '{path}'.");
            var target = new FileInfo(next).LinkTarget
                ?? throw new IOException($"Could not resolve symbolic link '{next}'.");
            if (Path.IsPathRooted(target))
                current = "/";
            foreach (var component in target.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
                pending.Push(component);
        }
        return current;
    }
}
