using Alcoler.Workbench.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Alcoler.Workbench.ViewModel;

public enum ChatGptPanelState
{
    SignedOut,
    Waiting,
    SignedIn,
    Failed,
}

public sealed partial class ChatGptAccountViewModel : ObservableObject
{
    private readonly ChatGptAccountService _service;
    private readonly Action<IReadOnlyList<string>> _applyModels;
    private ChatGptSignInTransaction? _transaction;

    public ChatGptAccountViewModel(ChatGptAccountService service, Action<IReadOnlyList<string>> applyModels)
    {
        _service = service;
        _applyModels = applyModels;
        RefreshStatus();
    }

    /// <summary>Set by the view; opens the authorization page in the system browser.</summary>
    public Func<Uri, Task>? OpenAuthorizeUrl { get; set; }

    [ObservableProperty] private ChatGptPanelState _state;
    [ObservableProperty] private string? _accountEmail;
    [ObservableProperty] private string? _accountName;
    [ObservableProperty] private string? _accountPlan;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string? _authorizeUrl;

    public bool IsSignedIn => State == ChatGptPanelState.SignedIn;
    public bool IsNotSignedIn => State != ChatGptPanelState.SignedIn;
    public bool IsWaiting => State == ChatGptPanelState.Waiting;
    public bool HasFailed => State == ChatGptPanelState.Failed;
    public string? AccountDisplay => AccountEmail ?? AccountName;
    public bool HasAccountDisplay => AccountDisplay is { Length: > 0 };

    partial void OnStateChanged(ChatGptPanelState value)
    {
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(IsNotSignedIn));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(HasFailed));
    }

    partial void OnAccountEmailChanged(string? value) => RefreshAccountDisplay();
    partial void OnAccountNameChanged(string? value) => RefreshAccountDisplay();

    private void RefreshAccountDisplay()
    {
        OnPropertyChanged(nameof(AccountDisplay));
        OnPropertyChanged(nameof(HasAccountDisplay));
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (_transaction is not null) return;
        var transaction = _service.StartSignIn();
        _transaction = transaction;
        State = ChatGptPanelState.Waiting;
        Message = null;
        AuthorizeUrl = transaction.AuthorizeUrl;
        await OpenAuthorizationPageAsync();
        try
        {
            var outcome = await transaction.Completion;
            if (outcome.Completed)
            {
                RefreshStatus();
                // An unentitled sign-in stays signed in but must never infer.
                Message = outcome.Entitled ? null
                    : "Signed in, but this account cannot use ChatGPT plan-based inference.";
                if (outcome.Entitled)
                    _applyModels(outcome.ModelIds);
            }
            else
            {
                RefreshStatus();
                State = ChatGptPanelState.Failed;
                Message = DescribeFailure(outcome);
            }
        }
        finally
        {
            _transaction = null;
            transaction.Dispose();
        }
    }

    [RelayCommand]
    private Task OpenAuthorizationPageAsync()
    {
        if (AuthorizeUrl is not { Length: > 0 } url || OpenAuthorizeUrl is not { } open
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return Task.CompletedTask;
        }
        // A browser failure does not fail the transaction; the URL stays copyable.
        return open(uri).ContinueWith(task => { }, TaskScheduler.Default);
    }

    [RelayCommand]
    private void CancelSignIn() => _transaction?.Cancel();

    [RelayCommand]
    private async Task SignOutAsync()
    {
        await _service.SignOutAsync();
        RefreshStatus();
        Message = null;
    }

    private void RefreshStatus()
    {
        var status = _service.GetStatus();
        AccountEmail = status.Account?.Email;
        AccountName = status.Account?.Name;
        AccountPlan = status.Account?.PlanType;
        State = status.SignedIn ? ChatGptPanelState.SignedIn : ChatGptPanelState.SignedOut;
        if (status.SignedIn && !status.Entitled)
            Message = "This account cannot use ChatGPT plan-based inference.";
    }

    private static string DescribeFailure(ChatGptSignInOutcome outcome) => outcome.Failure switch
    {
        ChatGptSignInFailure.Denied => "Authorization was denied.",
        ChatGptSignInFailure.Timeout => "Sign-in timed out.",
        ChatGptSignInFailure.Cancelled => "Sign-in was cancelled.",
        ChatGptSignInFailure.CredentialWriteFailed => "Could not store the ChatGPT credentials.",
        ChatGptSignInFailure.Network => $"Network error during sign-in. {outcome.Message}".Trim(),
        ChatGptSignInFailure.ServerError => $"The sign-in server rejected the request. {outcome.Message}".Trim(),
        ChatGptSignInFailure.Protocol => $"The sign-in response was invalid. {outcome.Message}".Trim(),
        _ => outcome.Message ?? "Sign-in failed.",
    };
}
