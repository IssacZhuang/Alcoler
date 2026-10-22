using System.Text.Json;
using System.Text.Json.Serialization;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Permissions;
using CommunityToolkit.Mvvm.ComponentModel;
using Alcoler.Contracts.Tools;
using Alcoler.Contracts.Identifiers;

namespace Alcoler.ViewModel;

public sealed partial class PendingInteractionViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions IndentedJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        TypeInfoResolver = JsonElementContext.Default,
    };
    private IReadOnlyList<QuestionDraftViewModel> _drafts = [];

    private PendingInteractionViewModel(InteractionId interactionId)
    {
        InteractionId = interactionId;
    }

    public InteractionId InteractionId { get; }
    public bool IsPermission { get; private init; }
    public bool AllowsFeedback { get; private init; }
    public string ToolName { get; private init; } = string.Empty;
    public RiskLevel RiskLevel { get; private init; }
    public string? PlanMarkdown { get; private init; }
    public bool HasPlanPreview => !string.IsNullOrWhiteSpace(PlanMarkdown);
    public string AllowActionText => AllowsFeedback ? "Approve" : "Allow";
    public string DenyActionText => AllowsFeedback ? "Request changes" : "Deny";
    public string PermissionTitle => AllowsFeedback ? "Plan ready for approval" : "Permission required";
    public string PermissionHint => AllowsFeedback
        ? "Enter to approve / Esc to request changes"
        : "Enter to allow / Esc to deny";

    [ObservableProperty]
    private string _feedback = string.Empty;

    [ObservableProperty]
    private string _inputJson = string.Empty;

    [ObservableProperty]
    private string _reason = string.Empty;

    [ObservableProperty]
    private string _prompt = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<InteractionQuestion> _questions = [];

    [ObservableProperty]
    private IReadOnlyDictionary<string, string> _selectedAnswers = new Dictionary<string, string>();

    [ObservableProperty]
    private int _currentQuestionIndex;

    [ObservableProperty]
    private int _activeOptionIndex = -1;

    public QuestionDraftViewModel? CurrentQuestion => _drafts.ElementAtOrDefault(CurrentQuestionIndex);
    public bool HasPreviews => CurrentQuestion?.HasPreviews == true;
    public string? ActivePreview => HasPreviews && CurrentQuestion is { } question
        ? (ActiveOptionIndex >= 0 && ActiveOptionIndex < question.Options.Count
            ? question.Options[ActiveOptionIndex].Preview
            : question.Options.FirstOrDefault(option => option.IsSelected)?.Preview ?? question.Options[0].Preview)
        : null;
    public IReadOnlyDictionary<string, InteractionAnnotation> SelectedAnnotations => _drafts
        .Where(question => question.HasAnswer && question.HasPreviews)
        .ToDictionary(question => question.Header, question => new InteractionAnnotation(
            question.Options.FirstOrDefault(option => option.IsSelected)?.Preview,
            string.IsNullOrWhiteSpace(question.PreviewNotes) ? null : question.PreviewNotes.Trim()));
    public int QuestionCount => _drafts.Count;
    public bool HasQuestions => QuestionCount > 0;
    public string PageText => $"{Math.Min(CurrentQuestionIndex + 1, Math.Max(QuestionCount, 1))} / {Math.Max(QuestionCount, 1)}";
    public bool CanMovePrevious => CurrentQuestionIndex > 0;
    public bool CanMoveNext => CurrentQuestionIndex < QuestionCount - 1;
    public string PrimaryActionText => CanMoveNext ? "Continue" : "Submit";
    public bool CanSubmit => !IsPermission;

    public static PendingInteractionViewModel FromPermission(PermissionRequest request)
    {
        var vm = new PendingInteractionViewModel(request.InteractionId)
        {
            IsPermission = true,
            AllowsFeedback = request.AllowsFeedback,
            ToolName = request.ToolName,
            RiskLevel = request.RiskLevel,
            PlanMarkdown = request.AllowsFeedback ? ReadPlan(request.Input) : null,
        };
        vm.InputJson = JsonSerializer.Serialize(request.Input, IndentedJson);
        vm.Reason = request.Reason;
        return vm;
    }

    private static string? ReadPlan(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty("plan", out var plan)
        && plan.ValueKind == JsonValueKind.String
            ? plan.GetString()
            : null;

    public static PendingInteractionViewModel FromInteraction(InteractionRequest request)
    {
        var vm = new PendingInteractionViewModel(request.InteractionId)
        {
            IsPermission = false,
        };
        vm.Prompt = request.Prompt;
        vm.Questions = request.Questions;
        return vm;
    }

    public void SelectAnswer(string header, string value)
    {
        var draft = _drafts.FirstOrDefault(question => question.Header == header);
        draft?.SetOptionSelected(value, true);
    }

    public void SetOptionSelected(InteractionQuestion question, string value, bool selected) =>
        _drafts.FirstOrDefault(draft => draft.Source == question)?.SetOptionSelected(value, selected);

    public bool ActivateOption(int index)
    {
        if (CurrentQuestion is not { } question || index < 0 || index >= question.Options.Count)
            return false;
        ActiveOptionIndex = index;
        var option = question.Options[index];
        question.SetOptionSelected(option.Label, !question.MultiSelect || !option.IsSelected);
        OnPropertyChanged(nameof(ActivePreview));
        return !question.MultiSelect && !question.HasPreviews && ContinueQuestion();
    }

    public bool ContinueQuestion()
    {
        AutoSelectActiveOption();
        if (!CanMoveNext)
            return true;
        CurrentQuestionIndex++;
        return false;
    }

    public void MovePrevious()
    {
        if (CanMovePrevious)
            CurrentQuestionIndex--;
    }

    public void MoveNext()
    {
        if (!CanMoveNext)
            return;
        AutoSelectActiveOption();
        CurrentQuestionIndex++;
    }

    public void MoveSelection(int direction)
    {
        if (CurrentQuestion is not { } question)
            return;
        var count = question.Options.Count + 1;
        ActiveOptionIndex = ActiveOptionIndex < 0
            ? direction > 0 ? 0 : count - 1
            : (ActiveOptionIndex + direction + count) % count;
    }

    private void AutoSelectActiveOption()
    {
        if (CurrentQuestion is { MultiSelect: false, HasAnswer: false } question
            && ActiveOptionIndex >= 0 && ActiveOptionIndex < question.Options.Count)
            question.SetOptionSelected(question.Options[ActiveOptionIndex].Label, true);
    }

    private void RefreshAnswers()
    {
        SelectedAnswers = _drafts.Where(question => question.HasAnswer)
            .ToDictionary(question => question.Header, question => question.Answer);
        OnPropertyChanged(nameof(SelectedAnnotations));
        OnPropertyChanged(nameof(ActivePreview));
    }

    private void RefreshPage()
    {
        OnPropertyChanged(nameof(CurrentQuestion));
        OnPropertyChanged(nameof(HasPreviews));
        OnPropertyChanged(nameof(ActivePreview));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(CanMovePrevious));
        OnPropertyChanged(nameof(CanMoveNext));
        OnPropertyChanged(nameof(PrimaryActionText));
        var question = CurrentQuestion;
        var preferred = -1;
        if (question is not null)
        {
            for (var index = 0; index < question.Options.Count; index++)
            {
                if (!question.Options[index].IsSelected)
                    continue;
                preferred = index;
                break;
            }
        }
        if (preferred < 0 && question?.IsCustomSelected == true)
            preferred = question.Options.Count;
        ActiveOptionIndex = preferred;
        UpdateActiveOption();
    }

    private void UpdateActiveOption()
    {
        if (CurrentQuestion is not { } question)
            return;
        foreach (var option in question.Options)
            option.IsActive = option.Index == ActiveOptionIndex;
        question.IsCustomActive = ActiveOptionIndex == question.Options.Count;
        OnPropertyChanged(nameof(ActivePreview));
    }

    partial void OnQuestionsChanged(IReadOnlyList<InteractionQuestion> value)
    {
        _drafts = value.Select(question => new QuestionDraftViewModel(question, RefreshAnswers)).ToArray();
        CurrentQuestionIndex = 0;
        SelectedAnswers = new Dictionary<string, string>();
        OnPropertyChanged(nameof(QuestionCount));
        OnPropertyChanged(nameof(HasQuestions));
        RefreshPage();
    }

    partial void OnCurrentQuestionIndexChanged(int value) => RefreshPage();
    partial void OnActiveOptionIndexChanged(int value) => UpdateActiveOption();
}

public sealed partial class QuestionDraftViewModel : ObservableObject
{
    private readonly Action _answerChanged;
    private string _answer = string.Empty;
    private bool _answerDirty = true;

    internal QuestionDraftViewModel(InteractionQuestion source, Action answerChanged)
    {
        Source = source;
        _answerChanged = answerChanged;
        Options = source.Options.Select((option, index) => new QuestionOptionViewModel(option, index, source.MultiSelect)).ToArray();
    }

    public InteractionQuestion Source { get; }
    public string Header => Source.Header;
    public string Text => Source.Text;
    public bool MultiSelect => Source.MultiSelect;
    public bool HasPreviews => !MultiSelect && Options.Any(option => !string.IsNullOrEmpty(option.Preview));
    public IReadOnlyList<QuestionOptionViewModel> Options { get; }
    public string CustomNumber => $"{Options.Count + 1}.";
    public bool IsCustomSelected => !string.IsNullOrWhiteSpace(CustomAnswer);
    public bool HasAnswer => IsCustomSelected || Options.Any(option => option.IsSelected);
    public string Answer
    {
        get
        {
            if (_answerDirty)
            {
                _answer = BuildAnswer();
                _answerDirty = false;
            }
            return _answer;
        }
    }

    private string BuildAnswer()
    {
        var labels = new List<string>(Options.Count + 1);
        foreach (var option in Options)
        {
            if (option.IsSelected)
                labels.Add(option.Label);
        }
        if (IsCustomSelected)
            labels.Add(CustomAnswer.Trim());
        return string.Join(", ", labels);
    }

    [ObservableProperty]
    private string _customAnswer = string.Empty;

    [ObservableProperty]
    private string _previewNotes = string.Empty;

    partial void OnPreviewNotesChanged(string value) => _answerChanged();

    [ObservableProperty]
    private bool _isCustomActive;

    internal void SetOptionSelected(string value, bool selected)
    {
        if (Options.FirstOrDefault(option => option.Label == value) is not { } target)
            return;
        if (!MultiSelect)
        {
            if (!selected)
                return;
            foreach (var option in Options)
                option.IsSelected = option == target;
            CustomAnswer = string.Empty;
        }
        else
        {
            target.IsSelected = selected;
        }
        NotifyAnswerChanged();
    }

    partial void OnCustomAnswerChanged(string value)
    {
        if (!MultiSelect && !string.IsNullOrWhiteSpace(value))
            foreach (var option in Options)
                option.IsSelected = false;
        NotifyAnswerChanged();
    }

    private void NotifyAnswerChanged()
    {
        _answerDirty = true;
        OnPropertyChanged(nameof(IsCustomSelected));
        OnPropertyChanged(nameof(HasAnswer));
        OnPropertyChanged(nameof(Answer));
        _answerChanged();
    }
}

public sealed partial class QuestionOptionViewModel : ObservableObject
{
    internal QuestionOptionViewModel(InteractionOption source, int index, bool multiSelect)
    {
        Label = source.Label;
        Description = source.Description;
        Preview = source.Preview;
        Index = index;
        MultiSelect = multiSelect;
    }

    public string Label { get; }
    public string? Description { get; }
    public string? Preview { get; }
    public string InlineDescription => string.IsNullOrEmpty(Description) ? string.Empty : $"  {Description}";
    public int Index { get; }
    public string Number => $"{Index + 1}.";
    public bool MultiSelect { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isActive;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class JsonElementContext : JsonSerializerContext;
