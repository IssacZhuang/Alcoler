using System.Text.Json;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Permissions;
using CommunityToolkit.Mvvm.ComponentModel;
using Alcoler.Contracts.Tools;
using Alcoler.Contracts.Identifiers;

namespace Alcoler.Workbench.ViewModel;

/// <summary>
/// Presentation state for a pending permission request or interaction question. Commands
/// are owned by <see cref="ConversationViewModel"/>; this type only carries display data.
/// </summary>
public sealed partial class PendingInteractionViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions IndentedJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private PendingInteractionViewModel(InteractionId interactionId)
    {
        InteractionId = interactionId;
    }

    public InteractionId InteractionId { get; }

    public bool IsPermission { get; private init; }

    public string ToolName { get; private init; } = string.Empty;

    public RiskLevel RiskLevel { get; private init; }

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

    public static PendingInteractionViewModel FromPermission(PermissionRequest request)
    {
        var vm = new PendingInteractionViewModel(request.InteractionId)
        {
            IsPermission = true,
            ToolName = request.ToolName,
            RiskLevel = request.RiskLevel,
        };
        vm.InputJson = JsonSerializer.Serialize(request.Input, IndentedJson);
        vm.Reason = request.Reason;
        return vm;
    }

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

    private readonly Dictionary<string, HashSet<string>> _multipleAnswers = [];

    public bool CanSubmit => Questions.Count > 0
        && Questions.All(question => SelectedAnswers.TryGetValue(question.Header, out var answer)
            && !string.IsNullOrWhiteSpace(answer));

    public void SelectAnswer(string header, string value)
    {
        if (Questions.FirstOrDefault(question => question.Header == header) is not { } question
            || !question.Options.Any(option => option.Label == value))
        {
            return;
        }
        if (question.MultiSelect)
        {
            SetOptionSelected(question, value, true);
            return;
        }
        SetAnswer(header, value);
    }

    public void SetOptionSelected(InteractionQuestion question, string value, bool selected)
    {
        if (!Questions.Contains(question) || !question.Options.Any(option => option.Label == value))
        {
            return;
        }
        if (!question.MultiSelect)
        {
            if (selected)
            {
                SetAnswer(question.Header, value);
            }
            return;
        }
        if (!_multipleAnswers.TryGetValue(question.Header, out var values))
        {
            values = [];
            _multipleAnswers[question.Header] = values;
        }
        if (selected)
        {
            values.Add(value);
        }
        else
        {
            values.Remove(value);
        }
        SetAnswer(question.Header, string.Join(", ", question.Options
            .Where(option => values.Contains(option.Label)).Select(option => option.Label)));
    }

    private void SetAnswer(string header, string value)
    {
        var answers = new Dictionary<string, string>(SelectedAnswers);
        if (string.IsNullOrEmpty(value))
        {
            answers.Remove(header);
        }
        else
        {
            answers[header] = value;
        }
        SelectedAnswers = answers;
    }

    partial void OnSelectedAnswersChanged(IReadOnlyDictionary<string, string> value) =>
        OnPropertyChanged(nameof(CanSubmit));
}
