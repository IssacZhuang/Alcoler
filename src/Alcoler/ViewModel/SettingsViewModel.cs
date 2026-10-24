using System.Collections.ObjectModel;
using System.Text;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Skills;
using Alcoler.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Alcoler.ViewModel;

public sealed record ProviderApiFormat(string Kind, string Label);

public sealed record ShellChoice(string Label, ShellEnvironment? Environment = null);

public sealed record LanguageChoice(string Value, string Display);

public sealed partial class ProviderViewModel : ObservableObject
{
    private readonly Action<ProviderViewModel> _removeFromParent;

    [ObservableProperty] private string _kind = string.Empty;
    [ObservableProperty] private string _baseUrl = string.Empty;
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private string? _id;
    [ObservableProperty] private string? _name;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string? _apiKeyManagementUrl;
    [ObservableProperty] private bool _isApiKeyVisible;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string? _accessType;

    public ProviderUsageViewModel Usage { get; } = new();
    public string? TemplateId { get; set; }
    public string EffectiveId => string.IsNullOrWhiteSpace(Id) ? Kind : Id;
    public string Label => !string.IsNullOrWhiteSpace(Name)
        ? Name
        : string.IsNullOrWhiteSpace(Kind) ? Loc.Get("settings_new_provider") : Kind;
    public string StatusLabel => Enabled ? Loc.Get("common_enabled") : Loc.Get("common_disabled");
    public char ApiKeyPasswordChar => IsApiKeyVisible ? '\0' : '*';
    public string ApiKeyVisibilityLabel => IsApiKeyVisible
        ? Loc.Get("settings_hide_api_key")
        : Loc.Get("settings_show_api_key");
    public bool HasApiKeyManagementUrl => ApiKeyManagementUri is not null;
    public bool IsChatGptAccount => AccessType == ProviderTemplate.ChatGptAccount;
    public Uri? ApiKeyManagementUri => Uri.TryCreate(ApiKeyManagementUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo) ? uri : null;
    public IReadOnlyList<ProviderApiFormat> ApiFormats => SettingsViewModel.ApiFormats;
    public ProviderApiFormat? SelectedApiFormat
    {
        get => ApiFormats.FirstOrDefault(format => format.Kind == Kind);
        set { if (value is not null) Kind = value.Kind; }
    }

    public ObservableCollection<ModelViewModel> Models { get; } = [];

    public ProviderViewModel(Action<ProviderViewModel> removeFromParent) => _removeFromParent = removeFromParent;

    partial void OnKindChanging(string value)
    {
        if (string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(Kind)) Id = Kind;
    }

    partial void OnKindChanged(string value)
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(EffectiveId));
        OnPropertyChanged(nameof(SelectedApiFormat));
        foreach (var model in Models) model.RefreshEffectiveProperties();
        ConfigureUsage();
    }

    partial void OnBaseUrlChanged(string value)
    {
        ConfigureUsage();
        foreach (var model in Models) model.RefreshEffectiveProperties();
    }
    partial void OnApiKeyChanged(string value) => ConfigureUsage();
    partial void OnIdChanged(string? value)
    {
        OnPropertyChanged(nameof(EffectiveId));
        ConfigureUsage();
    }
    private void ConfigureUsage() => Usage.Configure(EffectiveId, BaseUrl, ApiKey);
    partial void OnNameChanged(string? value) => OnPropertyChanged(nameof(Label));
    partial void OnEnabledChanged(bool value) => OnPropertyChanged(nameof(StatusLabel));
    partial void OnApiKeyManagementUrlChanged(string? value) => OnPropertyChanged(nameof(HasApiKeyManagementUrl));
    partial void OnAccessTypeChanged(string? value) => OnPropertyChanged(nameof(IsChatGptAccount));
    partial void OnIsApiKeyVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(ApiKeyPasswordChar));
        OnPropertyChanged(nameof(ApiKeyVisibilityLabel));
    }

    public WorkbenchProviderConfig ToConfig() => new(
        Kind.Trim(), BaseUrl.Trim(), string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim(),
        Models.Select(model => model.ToConfig()).ToList(), Id, Name, Enabled, ApiKeyManagementUrl, TemplateId,
        AccessType == ProviderTemplate.ChatGptAccount ? AccessType : null);

    internal WorkbenchProviderConfig ModelProviderConfig() => new(Kind.Trim(), BaseUrl.Trim(), null, [], Id, Name, Enabled,
        TemplateId: TemplateId, AccessType: AccessType == ProviderTemplate.ChatGptAccount ? AccessType : null);

    public ModelConfigurationViewModel CreateModelEditor(ModelViewModel? model = null) => new(this, model);

    public ModelViewModel CreateModel(WorkbenchModelConfig config) => new(model => Models.Remove(model), ModelProviderConfig, config) { Provider = this };

    [RelayCommand] private void RemoveProvider() => _removeFromParent(this);
    [RelayCommand] private void AddModel() => Models.Add(CreateModel(new WorkbenchModelConfig(string.Empty)));
    [RelayCommand] private void ToggleApiKeyVisibility() => IsApiKeyVisible = !IsApiKeyVisible;
    [RelayCommand] private void Rename() => IsRenaming = true;
    [RelayCommand]
    private void FinishRename()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();
        IsRenaming = false;
    }
}

public sealed partial class McpServerViewModel : ObservableObject
{
    private readonly Action<McpServerViewModel> _removeFromParent;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _command = string.Empty;

    [ObservableProperty]
    private string _argsLine = string.Empty;

    [ObservableProperty]
    private string _envText = string.Empty;

    public McpServerViewModel(Action<McpServerViewModel> removeFromParent) => _removeFromParent = removeFromParent;

    [RelayCommand]
    private void RemoveServer() => _removeFromParent(this);
}

public sealed partial class SkillToggleViewModel : ObservableObject
{
    public SkillToggleViewModel(SkillInfo skill, bool enabled)
    {
        Name = skill.Name;
        Description = skill.Description;
        Path = skill.SkPath;
        Enabled = enabled;
    }

    public string Name { get; }

    public string Description { get; }

    public string Path { get; }

    [ObservableProperty]
    private bool _enabled;
}

public interface IArchivedSessionMaintenance
{
    ValueTask<int> CountArchivedSessionsAsync(CancellationToken cancellationToken);

    ValueTask<int> DeleteArchivedSessionsAsync(CancellationToken cancellationToken);
}

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private const string Dark = "dark";
    private const string Light = "light";

    public static readonly IReadOnlyList<string> ProviderKinds = ["anthropic", "openai-compatible", "openai-responses"];
    public static readonly IReadOnlyList<ProviderApiFormat> ApiFormats =
    [
        new("anthropic", Loc.Get("settings_api_format_anthropic")),
        new("openai-compatible", Loc.Get("settings_api_format_openai_chat")),
        new("openai-responses", Loc.Get("settings_api_format_openai_responses")),
    ];

    public IReadOnlyList<ProviderTemplate> ProviderTemplates => WorkbenchModelCatalog.Templates;

    private readonly WorkbenchConfig _config;
    private readonly IArchivedSessionMaintenance? _archivedSessions;

    public ObservableCollection<int> AutoArchiveDayOptions { get; } = [3, 7, 14, 30];

    [ObservableProperty]
    private bool _autoArchiveSessions;

    [ObservableProperty]
    private int _autoArchiveOlderThanDays = 7;

    public SettingsViewModel(WorkbenchConfig config)
        : this(config, ShellResolver.AvailableShells(), OperatingSystem.IsWindows())
    {
    }

    internal SettingsViewModel(WorkbenchConfig config, Auth.ChatGptAccountService? chatGptAccount,
        SkillsCatalog? skillsCatalog = null, IArchivedSessionMaintenance? archivedSessions = null)
        : this(config, ShellResolver.AvailableShells(), OperatingSystem.IsWindows(), chatGptAccount, skillsCatalog,
            archivedSessions)
    {
    }

    internal SettingsViewModel(WorkbenchConfig config, IReadOnlyList<ShellEnvironment> shells, bool isWindows,
        Auth.ChatGptAccountService? chatGptAccount = null, SkillsCatalog? skillsCatalog = null,
        IArchivedSessionMaintenance? archivedSessions = null)
    {
        _config = config;
        _archivedSessions = archivedSessions;
        SupportsShellSelection = isWindows;
        ChatGpt = chatGptAccount is null ? null : new ChatGptAccountViewModel(chatGptAccount, ApplyChatGptModels);
        var choices = new List<ShellChoice> { new(Loc.Get("settings_shell_auto")) };
        choices.AddRange(shells.Select(shell => new ShellChoice(
            shell.Dialect == ShellDialect.Cmd ? "CMD" : shell.Name, shell)));
        if (config.IntegratedTerminalShell is { } savedShell
            && !choices.Any(choice => choice.Environment == savedShell))
            choices.Add(new ShellChoice(Loc.Get("settings_shell_saved_unavailable", savedShell.Name), savedShell));
        ShellChoices = choices;
        SelectedShell = choices.FirstOrDefault(choice => choice.Environment == config.IntegratedTerminalShell) ?? choices[0];
        SelectedLanguage = LanguageChoices.FirstOrDefault(choice =>
            string.Equals(choice.Value, config.Language, StringComparison.OrdinalIgnoreCase)) ?? LanguageChoices[0];
        Theme = config.Theme == Light ? Light : Dark;
        AutoArchiveSessions = config.AutoArchiveSessions;
        AutoArchiveOlderThanDays = Math.Clamp(config.AutoArchiveOlderThanDays, 1, 365);
        if (!AutoArchiveDayOptions.Contains(AutoArchiveOlderThanDays))
        {
            var options = AutoArchiveDayOptions.Append(AutoArchiveOlderThanDays).Order().ToArray();
            AutoArchiveDayOptions.Clear();
            foreach (var option in options)
            {
                AutoArchiveDayOptions.Add(option);
            }
        }
        LoadUserSkills(skillsCatalog ?? new SkillsCatalog());
        foreach (var provider in config.Providers ?? [])
        {
            var providerItem = new ProviderViewModel(RemoveProviderItem)
            {
                Kind = provider.Kind,
                BaseUrl = provider.BaseUrl,
                ApiKey = provider.ApiKey ?? string.Empty,
                Id = provider.Id,
                Name = provider.Name,
                Enabled = provider.Enabled,
                ApiKeyManagementUrl = provider.ApiKeyManagementUrl,
                TemplateId = provider.TemplateId,
                AccessType = provider.AccessType,
            };
            foreach (var model in provider.Models ?? [])
            {
                providerItem.Models.Add(providerItem.CreateModel(model));
            }
            Providers.Add(providerItem);
        }
        SelectedProvider = Providers.FirstOrDefault();
        foreach (var server in config.McpServers ?? [])
        {
            McpServers.Add(new McpServerViewModel(RemoveServerItem)
            {
                Name = server.Name,
                Command = server.Command,
                ArgsLine = JoinArgs(server.Args),
                EnvText = JoinEnv(server.Env),
            });
        }
    }

    public ObservableCollection<ProviderViewModel> Providers { get; } = [];

    public ObservableCollection<McpServerViewModel> McpServers { get; } = [];

    public ObservableCollection<SkillToggleViewModel> UserSkills { get; } = [];

    public string UserSkillSummary => UserSkills.Count == 1
        ? Loc.Get("settings_skill_count_one")
        : Loc.Get("settings_skill_count_many", UserSkills.Count);

    public ChatGptAccountViewModel? ChatGpt { get; }

    public IReadOnlyList<string> ThemeChoices { get; } = [Dark, Light];

    public IReadOnlyList<LanguageChoice> LanguageChoices { get; } =
    [
        new(Loc.Auto, Loc.Get("settings_language_auto")),
        .. Loc.SupportedLanguages.Select(language => new LanguageChoice(language.Code, language.NativeName)),
    ];

    public bool SupportsShellSelection { get; }

    public IReadOnlyList<ShellChoice> ShellChoices { get; }

    [ObservableProperty]
    private ShellChoice? _selectedShell;

    [ObservableProperty]
    private LanguageChoice? _selectedLanguage;

    [ObservableProperty]
    private ProviderViewModel? _selectedProvider;

    [ObservableProperty]
    private string _theme = Dark;

    [ObservableProperty]
    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public bool CanManageArchivedSessions => _archivedSessions is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArchivedSessionCountText))]
    [NotifyPropertyChangedFor(nameof(CanDeleteArchivedSessions))]
    private int _archivedSessionCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArchivedSessionCountText))]
    [NotifyPropertyChangedFor(nameof(CanDeleteArchivedSessions))]
    private bool _isArchivedSessionCountLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteArchivedSessions))]
    private bool _isDeletingArchivedSessions;

    public string ArchivedSessionCountText => IsArchivedSessionCountLoading
        ? Loc.Get("settings_archived_count_loading")
        : ArchivedSessionCount switch
        {
            0 => Loc.Get("settings_archived_count_none"),
            1 => Loc.Get("settings_archived_count_one"),
            _ => Loc.Get("settings_archived_count_many", ArchivedSessionCount),
        };

    public bool CanDeleteArchivedSessions =>
        _archivedSessions is not null &&
        ArchivedSessionCount > 0 &&
        !IsArchivedSessionCountLoading &&
        !IsDeletingArchivedSessions;

    public async Task LoadArchivedSessionCountAsync(CancellationToken cancellationToken = default)
    {
        if (_archivedSessions is null)
        {
            return;
        }

        IsArchivedSessionCountLoading = true;
        try
        {
            ArchivedSessionCount = await _archivedSessions.CountArchivedSessionsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = Loc.Get("settings_could_not_count_archived", exception.Message);
        }
        finally
        {
            IsArchivedSessionCountLoading = false;
        }
    }

    public async Task DeleteArchivedSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (_archivedSessions is null || IsDeletingArchivedSessions)
        {
            return;
        }

        IsDeletingArchivedSessions = true;
        try
        {
            await _archivedSessions.DeleteArchivedSessionsAsync(cancellationToken);
            ArchivedSessionCount = await _archivedSessions.CountArchivedSessionsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = Loc.Get("settings_could_not_delete_archived", exception.Message);
        }
        finally
        {
            IsDeletingArchivedSessions = false;
        }
    }

    [RelayCommand]
    private void AddProvider() => AddProviderFromTemplate(null);

    public void AddProviderFromTemplate(ProviderTemplate? template)
    {
        var provider = new ProviderViewModel(RemoveProviderItem)
        {
            Id = "provider-" + Guid.NewGuid().ToString("N"),
            Kind = template?.Kind ?? string.Empty,
            Name = template?.Name,
            BaseUrl = template?.BaseUrl ?? string.Empty,
            ApiKeyManagementUrl = template?.ApiKeyManagementUrl,
            TemplateId = template?.Id,
            AccessType = template?.AccessType,
        };
        foreach (var modelId in template?.ModelIds ?? [])
            provider.Models.Add(provider.CreateModel(new WorkbenchModelConfig(modelId)));
        Providers.Add(provider);
        SelectedProvider = provider;
    }

    [RelayCommand]
    private void AddServer() => McpServers.Add(new McpServerViewModel(RemoveServerItem));

    private void RemoveProviderItem(ProviderViewModel provider)
    {
        var index = Providers.IndexOf(provider);
        var wasSelected = SelectedProvider == provider;
        if (!Providers.Remove(provider)) return;
        provider.Usage.Dispose();
        if (wasSelected) SelectedProvider = Providers.Count == 0 ? null : Providers[Math.Min(index, Providers.Count - 1)];
    }

    private void RemoveServerItem(McpServerViewModel server) => McpServers.Remove(server);

    // Sign-in replaces the plan model list: the catalog is the account's source of truth.
    internal void ApplyChatGptModels(IReadOnlyList<string> modelIds)
    {
        var provider = Providers.FirstOrDefault(provider => provider.IsChatGptAccount);
        if (provider is null) return;
        provider.Models.Clear();
        foreach (var modelId in modelIds)
            provider.Models.Add(provider.CreateModel(new WorkbenchModelConfig(modelId)));
    }

    public void Dispose()
    {
        foreach (var provider in Providers) provider.Usage.Dispose();
    }

    public async Task<WorkbenchConfig?> SaveAsync(string? path = null)
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            Error = string.Join(Environment.NewLine, errors);
            return null;
        }

        var saved = BuildConfig();
        try
        {
            await Task.Run(() => saved.Save(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Loc.Get("settings_could_not_save", ex.Message);
            return null;
        }
        Error = null;
        return saved;
    }

    private List<string> Validate()
    {
        var errors = new List<string>();
        var providerIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Providers.Count; i++)
        {
            var provider = Providers[i];
            if (!ProviderKinds.Contains(provider.Kind.Trim(), StringComparer.OrdinalIgnoreCase))
                errors.Add(Loc.Get("settings_validation_provider_kind", i + 1, string.Join(", ", ProviderKinds)));
            if (!providerIds.Add(provider.EffectiveId.Trim()))
                errors.Add(Loc.Get("settings_validation_duplicate_provider", i + 1, provider.EffectiveId));
            var modelIds = new HashSet<string>(StringComparer.Ordinal);
            for (var j = 0; j < provider.Models.Count; j++)
            {
                var model = provider.Models[j];
                if (!modelIds.Add(model.ModelId.Trim()))
                    errors.Add(Loc.Get("settings_validation_duplicate_model", i + 1, model.ModelId.Trim()));
                errors.AddRange(ModelConfigurationValidation.Validate(model, provider.ToConfig())
                    .Select(error => Loc.Get("settings_validation_provider_model", i + 1, j + 1, error)));
            }
        }
        return errors;
    }

    private WorkbenchConfig BuildConfig() => _config with
    {
        Providers = Providers.Select(ToProviderConfig).ToList(),
        McpServers = McpServers.Select(ToMcpServerConfig).ToList(),
        Theme = Theme,
        Language = SelectedLanguage?.Value ?? Loc.Auto,
        IntegratedTerminalShell = SupportsShellSelection ? SelectedShell?.Environment : _config.IntegratedTerminalShell,
        DisabledSkillPaths = BuildDisabledSkillPaths(),
        AutoArchiveSessions = AutoArchiveSessions,
        AutoArchiveOlderThanDays = Math.Clamp(AutoArchiveOlderThanDays, 1, 365),
    };

    private void LoadUserSkills(SkillsCatalog catalog)
    {
        IReadOnlyList<SkillInfo> skills;
        try
        {
            skills = catalog.LoadUserSkills();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }
        var disabled = new HashSet<string>(
            (_config.DisabledSkillPaths ?? []).Select(SkillsCatalog.NormalizePath), DisabledPathComparer);
        foreach (var skill in skills
                     .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(skill => skill.Name, StringComparer.Ordinal))
        {
            UserSkills.Add(new SkillToggleViewModel(
                skill, !disabled.Contains(SkillsCatalog.NormalizePath(skill.SkPath))));
        }
    }

    private IReadOnlyList<string>? BuildDisabledSkillPaths()
    {
        var listed = new HashSet<string>(
            UserSkills.Select(skill => SkillsCatalog.NormalizePath(skill.Path)), DisabledPathComparer);
        var disabled = new List<string>();
        var seen = new HashSet<string>(DisabledPathComparer);
        void Add(string path)
        {
            var normalized = SkillsCatalog.NormalizePath(path);
            if (seen.Add(normalized))
                disabled.Add(normalized);
        }

        foreach (var path in _config.DisabledSkillPaths ?? [])
        {
            if (!listed.Contains(SkillsCatalog.NormalizePath(path)))
                Add(path);
        }
        foreach (var skill in UserSkills.Where(skill => !skill.Enabled))
            Add(skill.Path);
        return disabled.Count == 0 ? null : disabled;
    }

    private static StringComparer DisabledPathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static WorkbenchProviderConfig ToProviderConfig(ProviderViewModel provider) => provider.ToConfig();

    private static WorkbenchMcpServerConfig ToMcpServerConfig(McpServerViewModel server)
    {
        var args = SplitArgs(server.ArgsLine);
        var env = SplitEnv(server.EnvText);
        return new WorkbenchMcpServerConfig(
            server.Name.Trim(),
            server.Command.Trim(),
            args.Count == 0 ? null : args,
            env is { Count: 0 } ? null : env);
    }

    private static string JoinArgs(IReadOnlyList<string>? args) =>
        args is null || args.Count == 0
            ? string.Empty
            : string.Join(" ", args.Select(arg => arg.Contains(' ') ? $"\"{arg}\"" : arg));

    private static string JoinEnv(IReadOnlyDictionary<string, string>? env) =>
        env is null || env.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, env.Select(pair => $"{pair.Key}={pair.Value}"));

    // double quotes group whitespace-separated arguments
    private static List<string> SplitArgs(string? line)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(line))
        {
            return args;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        void AppendCurrent()
        {
            if (current.Length > 0)
            {
                args.Add(current.ToString());
                current.Clear();
            }
        }

        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                AppendCurrent();
            }
            else
            {
                current.Append(c);
            }
        }
        AppendCurrent();
        return args;
    }

    // lines without an equals sign or with an empty key are skipped
    private static Dictionary<string, string>? SplitEnv(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Dictionary<string, string>? env = null;
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            env ??= new Dictionary<string, string>(StringComparer.Ordinal);
            env[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return env;
    }
}
