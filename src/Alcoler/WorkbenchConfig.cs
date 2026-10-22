using System.Text.Json;
using System.Text.Json.Serialization;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Tools;

namespace Alcoler;

public sealed record WorkbenchModelConfig(
    string ModelId,
    int? ContextWindowTokens = null,
    string? DisplayName = null,
    bool Enabled = true,
    bool SmartConfiguration = true,
    int? MaxOutputTokens = null,
    bool? SupportsImage = null,
    bool? SupportsVideo = null,
    bool? SupportsAudio = null,
    bool? SupportsPdf = null,
    bool? SupportsJsonSchemaOutput = null,
    bool? SupportsNativeWebSearch = null,
    bool? SupportsMidConversationSystem = null,
    IReadOnlyList<string>? ReasoningLevels = null,
    string? ReasoningMapping = null,
    string? MaxOutputTokensMapping = null);

public sealed record WorkbenchProviderConfig(
    string Kind,
    string BaseUrl,
    string? ApiKey,
    IReadOnlyList<WorkbenchModelConfig> Models,
    string? Id = null,
    string? Name = null,
    bool Enabled = true,
    string? ApiKeyManagementUrl = null,
    string? TemplateId = null,
    string? AccessType = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveId => string.IsNullOrWhiteSpace(Id) ? Kind : Id;

    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(Name) ? Kind : Name;
}

public sealed record WorkbenchDefaultModelConfig(string ProviderId, string ModelId);

public sealed record WorkbenchComposerConfig(
    PermissionMode DefaultMode = PermissionMode.Build,
    IReadOnlyDictionary<string, PermissionMode>? PendingSessionModes = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, ModelSelection>>? ModelSelections = null,
    IReadOnlyDictionary<string, ModelSelection>? RecentModelSelections = null)
{
    public const string DraftSessionKey = "__draft__";
}

public sealed record WorkbenchMcpServerConfig(
    string Name,
    string Command,
    IReadOnlyList<string>? Args = null,
    IReadOnlyDictionary<string, string>? Env = null);

public sealed record WorkbenchConfig(
    IReadOnlyList<WorkbenchProviderConfig> Providers,
    WorkbenchDefaultModelConfig? DefaultModel = null,
    IReadOnlyList<WorkbenchMcpServerConfig>? McpServers = null,
    string Theme = "dark",
    string? BraveApiKey = null,
    WorkbenchComposerConfig? Composer = null,
    ShellEnvironment? IntegratedTerminalShell = null,
    IReadOnlyList<string>? DisabledSkillPaths = null)
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

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = WorkbenchConfigJsonContext.Default,
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkbenchConfig))]
internal sealed partial class WorkbenchConfigJsonContext : JsonSerializerContext;

public static class WorkbenchDataPaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".alcoler");

    public static string DatabasePath => Path.Combine(Root, "db", "db.sqlite");

    public static string ArtifactsPath => Path.Combine(Root, "artifacts");

    public static string ExecPath => Path.Combine(Root, "exec");
}
