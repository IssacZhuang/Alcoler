using System.Text;

namespace Alcoler.Kernel.Prompts;

/// <summary>
/// Resolves AGENTS.md sources for a workspace: the user-scope file first, then the nearest
/// workspace file found by walking from the workspace root up to the git project root
/// (inclusive of the root; to the filesystem root when no .git ancestor exists).
/// </summary>
public static class AgentInstructionsLoader
{
    private const string InstructionsFileName = "AGENTS.md";
    private const int MaxBytes = 100 * 1024;

    public static IReadOnlyList<AgentInstructionSource> Load(string workspaceRoot, string? userHome = null)
    {
        var home = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<(string FilePath, AgentInstructionScope Scope)>();
        var userFile = Path.Combine(home, ".alcoler", InstructionsFileName);
        if (IsFile(userFile))
            candidates.Add((userFile, AgentInstructionScope.User));
        if (FindWorkspaceInstructions(Path.GetFullPath(workspaceRoot)) is { } workspaceFile)
            candidates.Add((workspaceFile, AgentInstructionScope.Workspace));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<AgentInstructionSource>();
        foreach (var candidate in candidates)
        {
            if (!seen.Add(Path.GetFullPath(candidate.FilePath)))
                continue;
            if (TryRead(candidate.FilePath, candidate.Scope) is { } source)
                sources.Add(source);
        }
        return sources;
    }

    private static string? FindWorkspaceInstructions(string startDirectory)
    {
        var projectRoot = FindGitRoot(startDirectory);
        var current = startDirectory;
        while (true)
        {
            var candidate = Path.Combine(current, InstructionsFileName);
            if (IsFile(candidate))
                return candidate;
            var parent = GetParentDirectory(current);
            if (current == projectRoot || parent is null)
                return null;
            current = parent;
        }
    }

    private static string? FindGitRoot(string startDirectory)
    {
        // The filesystem root itself is never treated as a project root, matching the reference walk.
        var current = startDirectory;
        while (true)
        {
            var parent = GetParentDirectory(current);
            if (parent is null)
                return null;
            if (Directory.Exists(Path.Combine(current, ".git")) || File.Exists(Path.Combine(current, ".git")))
                return current;
            current = parent;
        }
    }

    private static string? GetParentDirectory(string directory)
    {
        var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        return string.IsNullOrEmpty(parent) || parent == trimmed ? null : parent;
    }

    private static bool IsFile(string path)
    {
        try
        {
            return File.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.Directory);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static AgentInstructionSource? TryRead(string path, AgentInstructionScope scope)
    {
        try
        {
            long sizeBytes;
            byte[] buffer;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                sizeBytes = stream.Length;
                buffer = new byte[Math.Min(sizeBytes, MaxBytes)];
                var bytesRead = 0;
                while (bytesRead < buffer.Length)
                {
                    var read = stream.Read(buffer, bytesRead, buffer.Length - bytesRead);
                    if (read <= 0)
                        break;
                    bytesRead += read;
                }
            }
            return new AgentInstructionSource(
                path,
                Path.GetFileName(path),
                Encoding.UTF8.GetString(buffer),
                sizeBytes > MaxBytes,
                scope);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
