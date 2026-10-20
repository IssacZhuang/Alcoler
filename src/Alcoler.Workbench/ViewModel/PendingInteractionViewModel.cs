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

    public void SelectAnswer(string header, string value)
    {
        var answers = new Dictionary<string, string>(SelectedAnswers) { [header] = value };
        SelectedAnswers = answers;
    }
}
