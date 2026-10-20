using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Alcoler.Workbench.ViewModel;

public sealed partial class ModelViewModel : ObservableObject
{
    private readonly Action<ModelViewModel> _removeFromParent;

    [ObservableProperty]
    private string _modelId = string.Empty;

    [ObservableProperty]
    private string _contextWindowTokens = string.Empty;

    /// <summary>Not editable in the dialog; carried through so saving does not erase it.</summary>
    public string? DisplayName { get; set; }

    public ModelViewModel(Action<ModelViewModel> removeFromParent) => _removeFromParent = removeFromParent;

    [RelayCommand]
    private void Remove() => _removeFromParent(this);
}

public sealed partial class ProviderViewModel : ObservableObject
{
    private readonly Action<ProviderViewModel> _removeFromParent;

    [ObservableProperty]
    private string _kind = string.Empty;

    [ObservableProperty]
    private string _baseUrl = string.Empty;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    public ObservableCollection<ModelViewModel> Models { get; } = [];

    public ProviderViewModel(Action<ProviderViewModel> removeFromParent) => _removeFromParent = removeFromParent;

    [RelayCommand]
    private void RemoveProvider() => _removeFromParent(this);

    [RelayCommand]
    private void AddModel() => Models.Add(new ModelViewModel(model => Models.Remove(model)));
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

public sealed partial class SettingsViewModel : ObservableObject
{
    private const string Dark = "dark";
    private const string Light = "light";

    public static readonly IReadOnlyList<string> ProviderKinds = ["anthropic", "openai-compatible"];

    private readonly WorkbenchDefaultModelConfig? _defaultModel;

    public SettingsViewModel(WorkbenchConfig config)
    {
        _defaultModel = config.DefaultModel;
        Theme = config.Theme == Light ? Light : Dark;
        foreach (var provider in config.Providers ?? [])
        {
            var providerItem = new ProviderViewModel(RemoveProviderItem)
            {
                Kind = provider.Kind,
                BaseUrl = provider.BaseUrl,
                ApiKey = provider.ApiKey ?? string.Empty,
            };
            foreach (var model in provider.Models ?? [])
            {
                providerItem.Models.Add(new ModelViewModel(model => providerItem.Models.Remove(model))
                {
                    ModelId = model.ModelId,
                    ContextWindowTokens = model.ContextWindowTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    DisplayName = model.DisplayName,
                });
            }
            Providers.Add(providerItem);
        }
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

    public IReadOnlyList<string> ThemeChoices { get; } = [Dark, Light];

    [ObservableProperty]
    private string _theme = Dark;

    [ObservableProperty]
    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    private void AddProvider() => Providers.Add(new ProviderViewModel(RemoveProviderItem));

    [RelayCommand]
    private void AddServer() => McpServers.Add(new McpServerViewModel(RemoveServerItem));

    private void RemoveProviderItem(ProviderViewModel provider) => Providers.Remove(provider);

    private void RemoveServerItem(McpServerViewModel server) => McpServers.Remove(server);

    public async Task<WorkbenchConfig?> SaveAsync(string? path = null)
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            Error = string.Join(Environment.NewLine, errors);
            return null;
        }

        var saved = BuildConfig();
        await Task.Run(() => saved.Save(path));
        Error = null;
        return saved;
    }

    private List<string> Validate()
    {
        var errors = new List<string>();
        for (var i = 0; i < Providers.Count; i++)
        {
            var provider = Providers[i];
            if (!ProviderKinds.Contains(provider.Kind.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"Provider {i + 1}: kind must be one of {string.Join(", ", ProviderKinds)}.");
            }
            for (var j = 0; j < provider.Models.Count; j++)
            {
                var model = provider.Models[j];
                if (string.IsNullOrWhiteSpace(model.ModelId))
                {
                    errors.Add($"Provider {i + 1} model {j + 1}: model id is required.");
                }
                var tokens = model.ContextWindowTokens.Trim();
                if (tokens.Length > 0 && !IsPositiveInt(tokens))
                {
                    errors.Add($"Provider {i + 1} model {j + 1}: context window must be a positive integer.");
                }
            }
        }
        return errors;
    }

    private WorkbenchConfig BuildConfig() => new(
        Providers.Select(ToProviderConfig).ToList(),
        _defaultModel,
        McpServers.Select(ToMcpServerConfig).ToList(),
        Theme);

    private static WorkbenchProviderConfig ToProviderConfig(ProviderViewModel provider) => new(
        provider.Kind.Trim(),
        provider.BaseUrl.Trim(),
        NullIfWhiteSpace(provider.ApiKey),
        provider.Models.Select(model => new WorkbenchModelConfig(
            model.ModelId.Trim(),
            int.TryParse(model.ContextWindowTokens.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var tokens)
                ? tokens
                : null,
            model.DisplayName)).ToList());

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

    private static bool IsPositiveInt(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0;

    private static string? NullIfWhiteSpace(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
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
