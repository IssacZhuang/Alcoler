namespace Alcoler.Adapters.Storage;

internal static class PathGuard
{
    // Windows path equality is case-insensitive; other platforms are case-sensitive.
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static bool IsUnderRoot(string candidate, string root)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(candidate, trimmed, Comparison)
            || candidate.Length > trimmed.Length
            && candidate[trimmed.Length] == Path.DirectorySeparatorChar
            && candidate.AsSpan(0, trimmed.Length).Equals(trimmed, Comparison);
    }
}
