using System.Collections.ObjectModel;
using System.Text.Json;
using Alcoler.Contracts.Browser;
using Alcoler.Contracts.Identifiers;
using Alcoler.Ui;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Alcoler.ViewModel;

public sealed partial class BrowserPaneViewModel : ObservableObject, IDisposable
{
    private static readonly JsonElement EmptyInput = JsonElement.Parse("{}");
    private readonly IBrowserController _controller;
    private readonly IUiScheduler _scheduler;
    private readonly Func<string, IImage?>? _imageLoader;
    private readonly Func<string, Control>? _pagePresenterFactory;
    private string? _loadedScreenshotUri;
    private DateTimeOffset _loadedScreenshotUpdatedAt;
    private string? _presenterTabId;
    private string? _presenterError;
    private int _bindingVersion;
    private bool _viewAttached;
    private bool _disposed;

    public BrowserPaneViewModel(
        IBrowserController controller,
        IUiScheduler scheduler,
        Func<string, IImage?>? imageLoader = null,
        Func<string, Control>? pagePresenterFactory = null)
    {
        _controller = controller;
        _scheduler = scheduler;
        _imageLoader = imageLoader;
        _pagePresenterFactory = pagePresenterFactory;
        _controller.StatusChanged += OnControllerStatusChanged;
    }

    public ObservableCollection<BrowserTabStatus> Tabs { get; } = [];

    [ObservableProperty]
    private SessionId? _activeSessionId;

    [ObservableProperty]
    private string _stateText = nameof(BrowserSessionState.Idle);

    [ObservableProperty]
    private string? _url;

    [ObservableProperty]
    private string? _title;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NavigateCommand))]
    private string _address = string.Empty;

    [ObservableProperty]
    private bool _isNavigating;

    [ObservableProperty]
    private string? _banner;

    [ObservableProperty]
    private string _updatedText = string.Empty;

    [ObservableProperty]
    private IImage? _screenshot;

    [ObservableProperty]
    private BrowserTabStatus? _selectedTab;

    [ObservableProperty]
    private string? _activeTabId;

    [ObservableProperty]
    private bool _isUserControlled;

    [ObservableProperty]
    private Control? _pagePresenter;

    public bool HasScreenshot => Screenshot is not null;

    public bool HasPagePresenter => PagePresenter is not null;

    public bool ShowScreenshot => !HasPagePresenter && HasScreenshot;

    public bool ShowEmptyState => !HasPagePresenter && !HasScreenshot;

    public bool CanInteract => !_disposed && ActiveSessionId is not null;

    public string OwnershipText => IsUserControlled
        ? "You are in control. Agent browser actions are paused."
        : "Agent controls the browser. Take over to pause automation.";

    public string EmptyStateText => _presenterError ?? (CanInteract
        ? _pagePresenterFactory is null
            ? "Embedded browser is unavailable in this host."
            : "Open a new tab or enter a URL to start browsing."
        : "Open a session to use the browser.");

    public void Bind(SessionId? sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ActiveSessionId = sessionId;
    }

    public BrowserPaneViewModel CreateBoundView(SessionId? sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var view = new BrowserPaneViewModel(_controller, _scheduler, _imageLoader, _pagePresenterFactory);
        view.Bind(sessionId);
        return view;
    }

    public void AttachView()
    {
        if (_disposed || _viewAttached)
        {
            return;
        }
        _viewAttached = true;
        UpdatePresenter();
    }

    public void DetachView()
    {
        _viewAttached = false;
        ReleasePresenter();
    }

    partial void OnScreenshotChanged(IImage? value)
    {
        OnPropertyChanged(nameof(HasScreenshot));
        NotifyViewportChanged();
    }

    partial void OnPagePresenterChanged(Control? value)
    {
        OnPropertyChanged(nameof(HasPagePresenter));
        NotifyViewportChanged();
    }

    private void NotifyViewportChanged()
    {
        OnPropertyChanged(nameof(ShowScreenshot));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyStateText));
    }

    partial void OnUrlChanged(string? value) => Address = value ?? string.Empty;

    partial void OnIsNavigatingChanged(bool value) => NotifyCommandsChanged();

    partial void OnActiveTabIdChanged(string? value) => NotifyCommandsChanged();

    partial void OnIsUserControlledChanged(bool value)
    {
        OnPropertyChanged(nameof(OwnershipText));
        NotifyCommandsChanged();
    }

    partial void OnActiveSessionIdChanged(SessionId? value)
    {
        _bindingVersion++;
        ReleasePresenter();
        _presenterError = null;
        IsNavigating = false;
        Banner = null;
        Url = null;
        Address = string.Empty;
        ClearScreenshot();
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(EmptyStateText));
        NotifyCommandsChanged();
        Refresh();
    }

    private void NotifyCommandsChanged()
    {
        NavigateCommand.NotifyCanExecuteChanged();
        NewTabCommand.NotifyCanExecuteChanged();
        SelectTabCommand.NotifyCanExecuteChanged();
        CloseTabCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        ReloadCommand.NotifyCanExecuteChanged();
        TakeOverCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
    }

    private bool CanExecute => CanInteract && !IsNavigating;

    private bool CanNavigate => CanExecute && !string.IsNullOrWhiteSpace(Address);

    private bool CanUseTab => CanExecute && ActiveTabId is not null;

    private bool CanTakeOver => CanExecute && !IsUserControlled;

    private bool CanResume => CanExecute && IsUserControlled;

    private bool CanSelectTab(BrowserTabStatus? tab) => CanExecute && tab is not null
        && tab.Id != ActiveTabId && Tabs.Any(current => current.Id == tab.Id);

    private bool CanCloseTab(BrowserTabStatus? tab) => CanExecute && tab is not null
        && Tabs.Any(current => current.Id == tab.Id);

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private Task NavigateAsync()
    {
        if (!CanNavigate)
        {
            return Task.CompletedTask;
        }
        var address = Address.Trim();
        if (!address.Contains("://", StringComparison.Ordinal)
            && !address.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            address = "https://" + address;
        }
        return ExecuteAsync(new BrowserNavigateAction(address) { TabId = ActiveTabId, UserInitiated = true }, "Navigation");
    }

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private Task NewTabAsync() => ExecuteAsync(Command("new_tab", null), "New tab");

    [RelayCommand(CanExecute = nameof(CanSelectTab))]
    private Task SelectTabAsync(BrowserTabStatus? tab) => CanSelectTab(tab)
        ? ExecuteAsync(Command("select_tab", tab!.Id), "Select tab") : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanCloseTab))]
    private Task CloseTabAsync(BrowserTabStatus? tab) => CanCloseTab(tab)
        ? ExecuteAsync(Command("close_tab", tab!.Id), "Close tab") : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanUseTab))]
    private Task BackAsync() => ExecuteAsync(Command("back", ActiveTabId), "Back");

    [RelayCommand(CanExecute = nameof(CanUseTab))]
    private Task ForwardAsync() => ExecuteAsync(Command("forward", ActiveTabId), "Forward");

    [RelayCommand(CanExecute = nameof(CanUseTab))]
    private Task ReloadAsync() => ExecuteAsync(Command("reload", ActiveTabId), "Reload");

    [RelayCommand(CanExecute = nameof(CanTakeOver))]
    private Task TakeOverAsync() => ExecuteAsync(null, "Take over");

    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task ResumeAsync() => ExecuteAsync(Command("resume", null), "Resume agent");

    private static BrowserCommandAction Command(string command, string? tabId) =>
        new(command, EmptyInput) { TabId = tabId, UserInitiated = true };

    private async Task ExecuteAsync(BrowserAction? action, string operation)
    {
        if (!CanExecute || ActiveSessionId is not { } sessionId)
        {
            return;
        }
        var version = _bindingVersion;
        Banner = null;
        IsNavigating = true;
        try
        {
            BrowserActionResult? result = null;
            if (action is null)
            {
                await _controller.TakeoverAsync(sessionId, CancellationToken.None);
            }
            else
            {
                result = await _controller.ExecuteAsync(sessionId, action, CancellationToken.None);
            }
            _scheduler.Post(() =>
            {
                if (!_disposed && _bindingVersion == version)
                {
                    Refresh();
                    if (result is { Ok: false })
                    {
                        Banner = result.Error ?? $"{operation} failed.";
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _scheduler.Post(() =>
            {
                if (!_disposed && _bindingVersion == version)
                {
                    Banner = $"{operation} failed: {ex.Message}";
                }
            });
        }
        finally
        {
            _scheduler.Post(() =>
            {
                if (!_disposed && _bindingVersion == version)
                {
                    IsNavigating = false;
                }
            });
        }
    }

    [RelayCommand]
    private void Refresh() => ApplyStatus(
        ActiveSessionId is { } sessionId ? _controller.GetStatus(sessionId) : null);

    private void OnControllerStatusChanged(object? sender, BrowserStatusChangedEventArgs e)
    {
        if (_disposed || ActiveSessionId is not { } sessionId || e.SessionId != sessionId)
        {
            return;
        }
        var version = _bindingVersion;
        _scheduler.Post(() =>
        {
            if (!_disposed && _bindingVersion == version && ActiveSessionId == e.SessionId)
            {
                ApplyStatus(e.Status);
            }
        });
    }

    private void ApplyStatus(BrowserSessionStatus? status)
    {
        if (_disposed)
        {
            return;
        }
        var tabs = status?.Tabs ?? [];
        var activeId = status?.ActiveTabId ?? tabs.FirstOrDefault(tab => tab.IsActive)?.Id;
        for (var index = 0; index < tabs.Count; index++)
        {
            var tab = tabs[index] with { IsActive = tabs[index].Id == activeId };
            if (index >= Tabs.Count)
            {
                Tabs.Add(tab);
            }
            else if (Tabs[index] != tab)
            {
                Tabs[index] = tab;
            }
        }
        while (Tabs.Count > tabs.Count)
        {
            Tabs.RemoveAt(Tabs.Count - 1);
        }
        ActiveTabId = activeId;
        SelectedTab = Tabs.FirstOrDefault(tab => tab.Id == activeId);
        IsUserControlled = status?.IsUserControlled == true || SelectedTab?.IsUserControlled == true;
        StateText = status?.State.ToString() ?? nameof(BrowserSessionState.Idle);
        Url = SelectedTab?.Url ?? status?.Url;
        Title = SelectedTab?.Title ?? status?.Title;
        UpdatedText = status is null ? string.Empty : RelativeTime.Format(status.UpdatedAt.ToUniversalTime());
        UpdatePresenter();
        Banner = status?.Error ?? _presenterError;
        NotifyCommandsChanged();
        if (status?.LatestScreenshotUri is not { } uri)
        {
            ClearScreenshot();
        }
        else if (!string.Equals(uri, _loadedScreenshotUri, StringComparison.OrdinalIgnoreCase)
            || status.UpdatedAt != _loadedScreenshotUpdatedAt)
        {
            LoadScreenshot(uri, status.UpdatedAt);
        }
    }

    private void UpdatePresenter()
    {
        if (!_viewAttached || _pagePresenterFactory is null || _presenterTabId == ActiveTabId)
        {
            return;
        }
        ReleasePresenter();
        _presenterError = null;
        _presenterTabId = ActiveTabId;
        if (ActiveTabId is { } tabId)
        {
            try
            {
                PagePresenter = _pagePresenterFactory(tabId);
            }
            catch (Exception ex)
            {
                _presenterError = $"Could not display the embedded browser. {ex.Message}";
                Banner = _presenterError;
            }
        }
        OnPropertyChanged(nameof(EmptyStateText));
    }

    private void ReleasePresenter()
    {
        var previous = PagePresenter;
        (previous as IDisposable)?.Dispose();
        PagePresenter = null;
        _presenterTabId = null;
    }

    private void LoadScreenshot(string uri, DateTimeOffset updatedAt)
    {
        if (_imageLoader is null)
        {
            return;
        }
        IImage? loaded;
        try
        {
            loaded = _imageLoader(uri);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (loaded is null)
        {
            return;
        }
        _loadedScreenshotUri = uri;
        _loadedScreenshotUpdatedAt = updatedAt;
        var previous = Screenshot;
        Screenshot = loaded;
        if (!ReferenceEquals(previous, loaded))
        {
            (previous as IDisposable)?.Dispose();
        }
    }

    private void ClearScreenshot()
    {
        _loadedScreenshotUri = null;
        _loadedScreenshotUpdatedAt = default;
        var previous = Screenshot;
        Screenshot = null;
        (previous as IDisposable)?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _bindingVersion++;
        _controller.StatusChanged -= OnControllerStatusChanged;
        DetachView();
        ClearScreenshot();
        OnPropertyChanged(nameof(CanInteract));
        NotifyCommandsChanged();
    }
}
