using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Alcoler.Adapters.Models;
using Alcoler.Contracts.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Alcoler.Workbench.ViewModel;

public sealed partial class ModelViewModel : ObservableObject
{
    private readonly Action<ModelViewModel> _removeFromParent;
    private readonly Func<WorkbenchProviderConfig> _provider;

    [ObservableProperty] private string _modelId = string.Empty;
    [ObservableProperty] private string _contextWindowTokens = string.Empty;
    [ObservableProperty] private string _maxOutputTokens = string.Empty;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private bool _smartConfiguration = true;
    [ObservableProperty] private bool? _supportsImage;
    [ObservableProperty] private bool? _supportsVideo;
    [ObservableProperty] private bool? _supportsAudio;
    [ObservableProperty] private bool? _supportsPdf;
    [ObservableProperty] private bool? _supportsJsonSchemaOutput;
    [ObservableProperty] private bool? _supportsNativeWebSearch;
    [ObservableProperty] private bool? _supportsMidConversationSystem;
    [ObservableProperty] private IReadOnlyList<string>? _reasoningLevels;
    [ObservableProperty] private string _reasoningMapping = string.Empty;
    [ObservableProperty] private string _maxOutputTokensMapping = string.Empty;

    public string? DisplayName { get; set; }
    public ProviderViewModel? Provider { get; internal set; }
    public ModelProperties EffectiveProperties => WorkbenchModelCatalog.Resolve(_provider(), ToConfig());
    public ModelProperties Defaults => WorkbenchModelCatalog.Defaults(_provider(), ModelId.Trim());
    public string ContextWindowPlaceholder => Defaults.ContextWindowTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    public string MaxOutputTokensPlaceholder => Defaults.DefaultMaxOutputTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    public string ReasoningMappingPlaceholder => Defaults.ReasoningMapping ?? string.Empty;
    public string ContextBadge => EffectiveProperties.ContextWindowTokens is { } tokens
        ? tokens >= 1000 ? (tokens / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "K" : tokens.ToString(CultureInfo.InvariantCulture)
        : "—";
    public string ContextDescription => $"Context window: {EffectiveProperties.ContextWindowTokens?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown"} tokens";
    public bool EffectiveSupportsImage { get => EffectiveProperties.SupportsImage; set => SupportsImage = value; }
    public bool EffectiveSupportsVideo { get => EffectiveProperties.SupportsVideo; set => SupportsVideo = value; }
    public bool EffectiveSupportsPdf { get => EffectiveProperties.SupportsPdf; set => SupportsPdf = value; }
    public bool EffectiveSupportsJsonSchemaOutput { get => EffectiveProperties.SupportsJsonSchemaOutput; set => SupportsJsonSchemaOutput = value; }
    public bool EffectiveSupportsNativeWebSearch { get => EffectiveProperties.SupportsNativeWebSearch; set => SupportsNativeWebSearch = value; }
    public bool EffectiveSupportsMidConversationSystem { get => EffectiveProperties.SupportsMidConversationSystem; set => SupportsMidConversationSystem = value; }
    public bool HasReasoning => EffectiveProperties.ReasoningLevels is { Count: > 1 };

    public ModelViewModel(Action<ModelViewModel> removeFromParent)
        : this(removeFromParent, () => new WorkbenchProviderConfig("anthropic", string.Empty, null, []), new WorkbenchModelConfig(string.Empty))
    {
    }

    internal ModelViewModel(Action<ModelViewModel> removeFromParent, Func<WorkbenchProviderConfig> provider, WorkbenchModelConfig config)
    {
        _removeFromParent = removeFromParent;
        _provider = provider;
        Apply(config);
    }

    public WorkbenchModelConfig ToConfig() => new(
        ModelId.Trim(), ParseTokens(ContextWindowTokens), DisplayName, Enabled, SmartConfiguration, ParseTokens(MaxOutputTokens),
        SupportsImage, SupportsVideo, SupportsAudio, SupportsPdf, SupportsJsonSchemaOutput, SupportsNativeWebSearch,
        SupportsMidConversationSystem, ReasoningLevels?.Select(level => level.Trim()).ToArray(), NullIfEmpty(ReasoningMapping), NullIfEmpty(MaxOutputTokensMapping));

    internal void Apply(WorkbenchModelConfig config)
    {
        ModelId = config.ModelId;
        ContextWindowTokens = FormatTokens(config.ContextWindowTokens);
        MaxOutputTokens = FormatTokens(config.MaxOutputTokens);
        DisplayName = config.DisplayName;
        Enabled = config.Enabled;
        SmartConfiguration = config.SmartConfiguration;
        SupportsImage = config.SupportsImage;
        SupportsVideo = config.SupportsVideo;
        SupportsAudio = config.SupportsAudio;
        SupportsPdf = config.SupportsPdf;
        SupportsJsonSchemaOutput = config.SupportsJsonSchemaOutput;
        SupportsNativeWebSearch = config.SupportsNativeWebSearch;
        SupportsMidConversationSystem = config.SupportsMidConversationSystem;
        ReasoningLevels = config.ReasoningLevels?.ToArray();
        ReasoningMapping = config.ReasoningMapping ?? string.Empty;
        MaxOutputTokensMapping = config.MaxOutputTokensMapping ?? string.Empty;
        RefreshEffectiveProperties();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(ModelId) or nameof(ContextWindowTokens) or nameof(MaxOutputTokens)
            or nameof(SupportsImage) or nameof(SupportsVideo) or nameof(SupportsPdf) or nameof(SupportsAudio)
            or nameof(SupportsJsonSchemaOutput) or nameof(SupportsNativeWebSearch) or nameof(SupportsMidConversationSystem)
            or nameof(ReasoningLevels) or nameof(ReasoningMapping) or nameof(MaxOutputTokensMapping))
        {
            RefreshEffectiveProperties();
        }
    }

    public void RefreshEffectiveProperties()
    {
        foreach (var property in new[]
        {
            nameof(EffectiveProperties), nameof(Defaults), nameof(ContextWindowPlaceholder), nameof(MaxOutputTokensPlaceholder),
            nameof(ReasoningMappingPlaceholder), nameof(ContextBadge), nameof(ContextDescription), nameof(EffectiveSupportsImage),
            nameof(EffectiveSupportsVideo), nameof(EffectiveSupportsPdf), nameof(EffectiveSupportsJsonSchemaOutput),
            nameof(EffectiveSupportsNativeWebSearch), nameof(EffectiveSupportsMidConversationSystem), nameof(HasReasoning),
        }) OnPropertyChanged(property);
    }

    internal static int? ParseTokens(string value) => int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    internal static string FormatTokens(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    internal static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [RelayCommand] private void Remove() => _removeFromParent(this);
}

public sealed partial class ReasoningLevelViewModel : ObservableObject
{
    private readonly ModelConfigurationViewModel _editor;
    [ObservableProperty] private string _value;

    public ReasoningLevelViewModel(ModelConfigurationViewModel editor, string value)
    {
        _editor = editor;
        _value = value;
    }

    partial void OnValueChanged(string value) => _editor.UpdateReasoningLevels();
    [RelayCommand] private void Remove() => _editor.ReasoningLevels.Remove(this);
    [RelayCommand] private void MoveUp() => _editor.MoveLevel(this, -1);
    [RelayCommand] private void MoveDown() => _editor.MoveLevel(this, 1);
}

public sealed partial class ModelConfigurationViewModel : ObservableObject
{
    private readonly ProviderViewModel _provider;
    private readonly ModelViewModel? _original;
    private bool _loading;
    private bool _committed;

    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isAdvancedExpanded;
    [ObservableProperty] private string _newReasoningLevel = string.Empty;

    public ModelViewModel Draft { get; }
    public ObservableCollection<ReasoningLevelViewModel> ReasoningLevels { get; } = [];
    public bool IsAdding => _original is null;
    public string Title => IsAdding ? "Add model configuration" : "Edit model configuration";
    public bool HasError => !string.IsNullOrEmpty(Error);
    public string SmartConfigurationHelp => "Smart configuration follows the model catalog. Only fields you change are stored as overrides. Turn it off to freeze the editable configuration. Restore defaults clears overrides without changing the model ID or enabled state.";

    public ModelConfigurationViewModel(ProviderViewModel provider, ModelViewModel? original = null)
    {
        _provider = provider;
        _original = original;
        Draft = new ModelViewModel(_ => { }, provider.ModelProviderConfig, original?.ToConfig() ?? new WorkbenchModelConfig(string.Empty));
        ReasoningLevels.CollectionChanged += OnReasoningCollectionChanged;
        LoadReasoningLevels();
        Draft.PropertyChanged += OnDraftChanged;
        if (!Draft.SmartConfiguration) FreezeEditableProperties();
    }

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    private void OnDraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading) return;
        if (e.PropertyName == nameof(ModelViewModel.SmartConfiguration))
        {
            if (Draft.SmartConfiguration) RestoreDefaults();
            else FreezeEditableProperties();
        }
        if (e.PropertyName == nameof(ModelViewModel.ModelId) && Draft.ReasoningLevels is null) LoadReasoningLevels();
        Error = null;
    }

    private void LoadReasoningLevels()
    {
        _loading = true;
        try
        {
            ReasoningLevels.Clear();
            foreach (var value in Draft.EffectiveProperties.ReasoningLevels ?? [])
                ReasoningLevels.Add(new ReasoningLevelViewModel(this, value));
        }
        finally { _loading = false; }
    }

    private void OnReasoningCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateReasoningLevels();

    internal void UpdateReasoningLevels()
    {
        if (!_loading) Draft.ReasoningLevels = ReasoningLevels.Select(level => level.Value).ToArray();
    }

    internal void MoveLevel(ReasoningLevelViewModel level, int direction)
    {
        var index = ReasoningLevels.IndexOf(level);
        var target = index + direction;
        if (index >= 0 && target >= 0 && target < ReasoningLevels.Count) ReasoningLevels.Move(index, target);
    }

    [RelayCommand]
    private void AddReasoningLevel()
    {
        ReasoningLevels.Add(new ReasoningLevelViewModel(this, NewReasoningLevel.Trim()));
        NewReasoningLevel = string.Empty;
    }

    private void FreezeEditableProperties()
    {
        var effective = Draft.EffectiveProperties;
        Draft.ContextWindowTokens = ModelViewModel.FormatTokens(effective.ContextWindowTokens);
        Draft.MaxOutputTokens = ModelViewModel.FormatTokens(effective.DefaultMaxOutputTokens);
        Draft.SupportsImage = effective.SupportsImage;
        Draft.SupportsVideo = effective.SupportsVideo;
        Draft.SupportsPdf = effective.SupportsPdf;
        Draft.SupportsJsonSchemaOutput = effective.SupportsJsonSchemaOutput;
        Draft.SupportsNativeWebSearch = effective.SupportsNativeWebSearch;
        Draft.SupportsMidConversationSystem = effective.SupportsMidConversationSystem;
        Draft.ReasoningLevels = effective.ReasoningLevels?.ToArray();
        Draft.ReasoningMapping = effective.ReasoningMapping ?? string.Empty;
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        _loading = true;
        try
        {
            Draft.Apply(new WorkbenchModelConfig(Draft.ModelId, DisplayName: Draft.DisplayName, Enabled: Draft.Enabled,
                SupportsAudio: Draft.SupportsAudio, MaxOutputTokensMapping: ModelViewModel.NullIfEmpty(Draft.MaxOutputTokensMapping)));
        }
        finally { _loading = false; }
        LoadReasoningLevels();
        Error = null;
    }

    public bool TrySave()
    {
        if (_committed) return false;
        var errors = ModelConfigurationValidation.Validate(Draft, _provider.ToConfig());
        if (!Draft.SmartConfiguration)
        {
            if (string.IsNullOrWhiteSpace(Draft.ContextWindowTokens)) errors.Add("Context window is required in manual mode.");
            if (string.IsNullOrWhiteSpace(Draft.MaxOutputTokens)) errors.Add("Max output tokens is required in manual mode.");
            if (string.IsNullOrWhiteSpace(Draft.ReasoningMapping)) errors.Add("Reasoning mapping is required in manual mode.");
        }
        if (_provider.Models.Any(model => model != _original && string.Equals(model.ModelId.Trim(), Draft.ModelId.Trim(), StringComparison.Ordinal)))
            errors.Add("A model with this model ID already exists in this provider.");
        if (errors.Count > 0)
        {
            Error = string.Join(Environment.NewLine, errors);
            IsAdvancedExpanded = true;
            return false;
        }
        if (_original is null) _provider.Models.Add(_provider.CreateModel(Draft.ToConfig()));
        else _original.Apply(Draft.ToConfig());
        _committed = true;
        Error = null;
        return true;
    }
}

internal static class ModelConfigurationValidation
{
    public static List<string> Validate(ModelViewModel model, WorkbenchProviderConfig provider)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(model.ModelId)) errors.Add("Model id is required.");
        ValidateTokens(model.ContextWindowTokens, "Context window", errors);
        ValidateTokens(model.MaxOutputTokens, "Max output tokens", errors);
        var config = model.ToConfig();
        if (config.ReasoningLevels is { } levels && (levels.Count == 0 || levels.Any(string.IsNullOrWhiteSpace)
            || levels.Select(level => level.Trim()).Distinct(StringComparer.Ordinal).Count() != levels.Count))
            errors.Add("Reasoning levels must be unique, nonempty values.");
        var effective = WorkbenchModelCatalog.Resolve(provider, config);
        ValidateMapping(config.ReasoningMapping, "Reasoning mapping", effective.ReasoningLevels, effective.DefaultMaxOutputTokens, errors);
        if (config.ReasoningLevels is not null && config.ReasoningMapping is null)
            ValidateMapping(effective.ReasoningMapping, "Reasoning mapping", config.ReasoningLevels, effective.DefaultMaxOutputTokens, errors);
        ValidateMapping(config.MaxOutputTokensMapping, "Max output tokens mapping", effective.ReasoningLevels, effective.DefaultMaxOutputTokens, errors);
        return errors;
    }

    private static void ValidateTokens(string value, string label, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(value) && ModelViewModel.ParseTokens(value) is not > 0)
            errors.Add($"{label} must be a positive integer.");
    }

    private static void ValidateMapping(string? expression, string label, IReadOnlyList<string>? levels, int? maxOutputTokens, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(expression)) return;
        try
        {
            ModelOptionMapping.Validate(expression);
            if (levels is { Count: > 0 })
            {
                foreach (var level in levels) ModelOptionMapping.Evaluate(expression, level.Trim(), maxOutputTokens ?? 4096);
            }
            else ModelOptionMapping.Evaluate(expression, null, maxOutputTokens ?? 4096);
        }
        catch (ArgumentException ex) { errors.Add($"{label}: {ex.Message}"); }
    }
}
