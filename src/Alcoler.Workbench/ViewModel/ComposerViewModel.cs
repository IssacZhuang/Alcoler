using Alcoler.Contracts.Permissions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Alcoler.Workbench.ViewModel;

public sealed partial class ComposerViewModel : ObservableObject
{
    [ObservableProperty]
    private string _draft = string.Empty;

    [ObservableProperty]
    private PermissionMode _mode = PermissionMode.Build;

    /// <summary>True while the session is running a turn; disables sending plain input.</summary>
    [ObservableProperty]
    private bool _isTurnActive;

    public bool CanSend => !IsTurnActive && !string.IsNullOrWhiteSpace(Draft);

    public void ClearIfSendable(string sentText)
    {
        if (string.Equals(Draft, sentText, StringComparison.Ordinal))
        {
            Draft = string.Empty;
        }
    }

    partial void OnDraftChanged(string value) => OnPropertyChanged(nameof(CanSend));

    partial void OnIsTurnActiveChanged(bool value) => OnPropertyChanged(nameof(CanSend));
}
