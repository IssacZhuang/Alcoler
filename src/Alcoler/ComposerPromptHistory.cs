using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alcoler;

public sealed class ComposerPromptHistory
{
    public const int MaxEntries = 30;

    private static readonly StringComparer WorkspaceComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<string>> _entries = new(WorkspaceComparer);
    private readonly Dictionary<string, string[]> _snapshots = new(WorkspaceComparer);

    public static string DefaultPath => Path.Combine(WorkbenchDataPaths.Root, "prompt-history.json");

    public ComposerPromptHistory(string? path = null)
    {
        _path = Path.GetFullPath(path ?? DefaultPath);
        Load();
    }

    public IReadOnlyList<string> GetEntries(string workspaceRoot)
    {
        var key = NormalizeRoot(workspaceRoot);
        lock (_gate)
        {
            if (_snapshots.TryGetValue(key, out var snapshot))
                return snapshot;
            if (!_entries.TryGetValue(key, out var entries))
                return [];
            snapshot = entries.ToArray();
            _snapshots[key] = snapshot;
            return snapshot;
        }
    }

    public void Record(string workspaceRoot, string text)
    {
        var key = NormalizeRoot(workspaceRoot);
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entries))
            {
                entries = [];
                _entries.Add(key, entries);
            }
            if (!Append(entries, text))
            {
                return;
            }
            _snapshots.Remove(key);
            Save();
        }
    }

    private static string NormalizeRoot(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
    }

    private static bool Append(List<string> entries, string text)
    {
        if (entries.Count > 0 && entries[^1] == text)
        {
            return false;
        }
        entries.Add(text);
        if (entries.Count > MaxEntries)
        {
            entries.RemoveRange(0, entries.Count - MaxEntries);
        }
        return true;
    }

    private void Load()
    {
        Dictionary<string, string[]>? stored;
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }
            stored = JsonSerializer.Deserialize(File.ReadAllText(_path), ComposerPromptHistoryJsonContext.Default.DictionaryStringStringArray);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return;
        }
        if (stored is null)
        {
            return;
        }

        foreach (var (root, prompts) in stored)
        {
            if (prompts is null)
            {
                continue;
            }
            string key;
            try
            {
                key = NormalizeRoot(root);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
            {
                continue;
            }
            if (!_entries.TryGetValue(key, out var entries))
            {
                entries = [];
                _entries.Add(key, entries);
            }
            foreach (var prompt in prompts)
            {
                if (!string.IsNullOrWhiteSpace(prompt))
                {
                    Append(entries, prompt.Trim());
                }
            }
        }
    }

    private void Save()
    {
        string? tempPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            tempPath = Path.Combine(directory, Path.GetRandomFileName());
            var stored = _entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), WorkspaceComparer);
            File.WriteAllText(tempPath, JsonSerializer.Serialize(stored, ComposerPromptHistoryJsonContext.Default.DictionaryStringStringArray));
            File.Move(tempPath, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // History persistence must not turn an accepted send into a failure.
        }
        finally
        {
            if (tempPath is not null)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }
}

[JsonSerializable(typeof(Dictionary<string, string[]>))]
internal sealed partial class ComposerPromptHistoryJsonContext : JsonSerializerContext;
