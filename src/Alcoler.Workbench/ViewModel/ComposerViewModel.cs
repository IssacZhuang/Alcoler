using Alcoler.Contracts.Permissions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Alcoler.Workbench.ViewModel;

public sealed partial class ComposerViewModel : ObservableObject
{
    [ObservableProperty]
    private string _draft = string.Empty;

    [ObservableProperty]
    private PermissionMode _mode = PermissionMode.Build;

    [ObservableProperty]
    private bool _isTurnActive;

    [ObservableProperty]
    private bool _isSending;

    [ObservableProperty]
    private PermissionMode _sessionMode = PermissionMode.Build;

    public string ModeLabel => Mode == PermissionMode.Yolo ? "Full access" : Mode.ToString();

    public bool IsFullAccess => Mode == PermissionMode.Yolo;

    public bool ShowStop => IsTurnActive && string.IsNullOrWhiteSpace(Draft);

    public bool CanSend => !IsSending && !string.IsNullOrWhiteSpace(Draft)
        && (!IsTurnActive || Mode == SessionMode);

    public string SendHint => IsTurnActive
        ? Mode == SessionMode ? "Queue message (Enter)" : "Stop the active turn before changing permissions"
        : "Send message (Enter)";

    public void ClearIfSendable(string sentText)
    {
        if (string.Equals(Draft, sentText, StringComparison.Ordinal))
        {
            Draft = string.Empty;
        }
    }

    partial void OnDraftChanged(string value)
    {
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(ShowStop));
    }

    partial void OnIsTurnActiveChanged(bool value) => NotifySendState();

    partial void OnIsSendingChanged(bool value) => OnPropertyChanged(nameof(CanSend));

    partial void OnSessionModeChanged(PermissionMode value) => NotifySendState();

    partial void OnModeChanged(PermissionMode value)
    {
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(IsFullAccess));
        NotifySendState();
    }

    private void NotifySendState()
    {
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(SendHint));
        OnPropertyChanged(nameof(ShowStop));
    }
}
