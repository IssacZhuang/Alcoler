namespace Alcoler.Adapters.Storage;

internal static class PathGuard
{
    // Windows path equality is case-insensitive; other platforms are case-sensitive.
    public static bool IsUnderRoot(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(candidate, trimmed, comparison)
            || candidate.StartsWith(trimmed + Path.DirectorySeparatorChar, comparison);
    }
}
