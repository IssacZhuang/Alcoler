using System.Text.Json;

namespace Alcoler.Workbench;

public sealed record WorkbenchModelConfig(string ModelId, int? ContextWindowTokens = null, string? DisplayName = null);

public sealed record WorkbenchProviderConfig(
    string Kind,
    string BaseUrl,
    string? ApiKey,
    IReadOnlyList<WorkbenchModelConfig> Models);

public sealed record WorkbenchDefaultModelConfig(string ProviderId, string ModelId);

public sealed record WorkbenchMcpServerConfig(
    string Name,
    string Command,
    IReadOnlyList<string>? Args = null,
    IReadOnlyDictionary<string, string>? Env = null);

public sealed record WorkbenchConfig(
    IReadOnlyList<WorkbenchProviderConfig> Providers,
    WorkbenchDefaultModelConfig? DefaultModel = null,
    IReadOnlyList<WorkbenchMcpServerConfig>? McpServers = null,
    string Theme = "dark")
{
    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".alcoler",
        "config.json");

    public static WorkbenchConfig LoadOrDefault(string? path = null)
    {
        path ??= DefaultConfigPath;
        if (!File.Exists(path))
        {
            return new WorkbenchConfig([]);
        }

        var json = File.ReadAllText(path);
        var parsed = JsonSerializer.Deserialize<WorkbenchConfig>(json, JsonOptions);
        if (parsed is null)
        {
            return new WorkbenchConfig([]);
        }
        return string.IsNullOrWhiteSpace(parsed.Theme) ? parsed with { Theme = "dark" } : parsed;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultConfigPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory, Path.GetRandomFileName());
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // temp cleanup is best effort; the original failure must propagate
            }
            throw;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public static class WorkbenchDataPaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".alcoler");

    public static string DatabasePath => Path.Combine(Root, "db", "db.sqlite");

    public static string ArtifactsPath => Path.Combine(Root, "artifacts");
}
