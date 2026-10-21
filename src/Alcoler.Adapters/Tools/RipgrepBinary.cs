using System.Text;

namespace Alcoler.Adapters.Tools;

/// <summary>
/// Locates an rg executable for Bash sessions: an existing system rg on PATH
/// wins (the shim is then unnecessary), otherwise the ALCOLER_RG_BINARY
/// override or the bundled tools/ripgrep binary is used.
/// </summary>
internal static class RipgrepBinary
{
    private static readonly Lock Gate = new();
    private static string? _bundled;
    private static bool _bundledResolved;

    public static string? LocateBundled() => LocateBundled(Environment.GetEnvironmentVariable);

    internal static string? LocateBundled(Func<string, string?> getEnvironmentVariable)
    {
        lock (Gate)
        {
            if (_bundledResolved)
                return _bundled;
            _bundledResolved = true;
            var overridePath = getEnvironmentVariable("ALCOLER_RG_BINARY");
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
                _bundled = overridePath;
            if (_bundled is null)
            {
                var candidate = Path.Combine(AppContext.BaseDirectory, "tools", "ripgrep",
                    OperatingSystem.IsWindows() ? "rg.exe" : "rg");
                _bundled = File.Exists(candidate) ? candidate : null;
            }
            return _bundled;
        }
    }

    public static string? FindOnPath() => FindOnPath(Environment.GetEnvironmentVariable, File.Exists);

    internal static string? FindOnPath(Func<string, string?> getEnvironmentVariable, Func<string, bool> fileExists)
    {
        var path = getEnvironmentVariable("PATH") ?? getEnvironmentVariable("Path") ?? "";
        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        string[] names = OperatingSystem.IsWindows() ? ["rg.exe", "rg"] : ["rg"];
        foreach (var directory in path.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var trimmed = directory.Trim('"');
            if (trimmed.Length == 0)
                continue;
            foreach (var name in names)
            {
                var candidate = Path.Combine(trimmed, name);
                if (fileExists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    internal static void ResetCacheForTests()
    {
        lock (Gate)
        {
            _bundled = null;
            _bundledResolved = false;
        }
    }
}

/// <summary>
/// POSIX shell preamble defining an rg function over the bundled binary when
/// the system has no rg. The reference implementation injects the same style
/// of shim into its shell sessions.
/// </summary>
internal static class BashRipgrepShim
{
    private static string? _cached;

    public static string Preamble()
    {
        if (_cached is null)
            _cached = Build();
        return _cached;
    }

    private static string Build()
    {
        if (RipgrepBinary.FindOnPath() is not null)
            return "";
        var binary = RipgrepBinary.LocateBundled();
        return binary is null ? "" : $"rg() {{ {Quote(binary)} \"$@\"; }}; ";
    }

    internal static string BuildForTests(Func<string, string?> getEnvironmentVariable, Func<string, bool> fileExists, string baseDirectory)
    {
        if (RipgrepBinary.FindOnPath(getEnvironmentVariable, fileExists) is not null)
            return "";
        var overridePath = getEnvironmentVariable("ALCOLER_RG_BINARY");
        var bundled = !string.IsNullOrWhiteSpace(overridePath) && fileExists(overridePath)
            ? overridePath
            : Path.Combine(baseDirectory, "tools", "ripgrep", OperatingSystem.IsWindows() ? "rg.exe" : "rg");
        return fileExists(bundled) ? $"rg() {{ {Quote(bundled)} \"$@\"; }}; " : "";
    }

    internal static void ResetCacheForTests() => _cached = null;

    private static string Quote(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            if (c is '\\' or '"' or '$' or '`')
                builder.Append('\\');
            builder.Append(c);
        }
        return builder.Append('"').ToString();
    }
}
