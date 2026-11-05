using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Alcoler.Contracts.Browser;
using Alcoler.Contracts.Identifiers;
using Alcoler.Localization;
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
    private readonly Func<string, Control>? _pagePresenterFactory;
    private readonly Func<Uri, Task<bool>> _uriLauncher;
    private string? _presenterTabId;
    private string? _presenterError;
    private int _bindingVersion;
    private bool _viewAttached;
    private bool _disposed;

    public BrowserPaneViewModel(
        IBrowserController controller,
        IUiScheduler scheduler,
        Func<string, Control>? pagePresenterFactory = null,
        Func<Uri, Task<bool>>? uriLauncher = null)
    {
        _controller = controller;
        _scheduler = scheduler;
        _pagePresenterFactory = pagePresenterFactory;
        _uriLauncher = uriLauncher ?? LaunchWithSystemBrowser;
        _controller.StatusChanged += OnControllerStatusChanged;
    }

    public ObservableCollection<BrowserTabStatus> Tabs { get; } = [];

    [ObservableProperty]
    private SessionId? _activeSessionId;

    [ObservableProperty]
    private string _stateText = Loc.Get("browser_state_idle");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenInBrowserCommand))]
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
    private Control? _pagePresenter;

    public bool HasScreenshot => Screenshot is not null;

    public bool HasPagePresenter => PagePresenter is not null;

    public bool ShowScreenshot => !HasPagePresenter && HasScreenshot;

    public bool ShowEmptyState => !HasPagePresenter && !HasScreenshot;

    public bool CanInteract => !_disposed && ActiveSessionId is not null;

    public string EmptyStateText => _presenterError ?? (CanInteract
        ? _pagePresenterFactory is null
            ? Loc.Get("browser_embedded_unavailable")
            : Loc.Get("browser_empty_hint")
        : Loc.Get("browser_open_session_first"));

    public void Bind(SessionId? sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ActiveSessionId = sessionId;
    }

    public BrowserPaneViewModel CreateBoundView(SessionId? sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var view = new BrowserPaneViewModel(_controller, _scheduler, _pagePresenterFactory, _uriLauncher);
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
        OpenInBrowserCommand.NotifyCanExecuteChanged();
    }

    private bool CanExecute => CanInteract && !IsNavigating;

    private bool CanNavigate => CanExecute && !string.IsNullOrWhiteSpace(Address);

    private bool CanUseTab => CanExecute && ActiveTabId is not null;

    private bool CanOpenInBrowser => !_disposed && Uri.TryCreate(Url, UriKind.Absolute, out _);

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
        return ExecuteAsync(new BrowserNavigateAction(address) { TabId = ActiveTabId, UserInitiated = true },
            Loc.Get("browser_op_navigation"));
    }

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private Task NewTabAsync() => ExecuteAsync(Command("new_tab", null), Loc.Get("browser_op_new_tab"));

    [RelayCommand(CanExecute = nameof(CanSelectTab))]
    private Task SelectTabAsync(BrowserTabStatus? tab) => CanSelectTab(tab)
        ? ExecuteAsync(Command("select_tab", tab!.Id), Loc.Get("browser_op_select_tab")) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanCloseTab))]
    private Task CloseTabAsync(BrowserTabStatus? tab) => CanCloseTab(tab)
        ? ExecuteAsync(Command("close_tab", tab!.Id), Loc.Get("browser_op_close_tab")) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanUseTab))]
    private Task BackAsync() => ExecuteAsync(Command("back", ActiveTabId), Loc.Get("browser_back"));

    [RelayCommand(CanExecute = nameof(CanUseTab))]
    private Task ForwardAsync() => ExecuteAsync(Command("forward", ActiveTabId), Loc.Get("browser_forward"));

    [RelayCommand(CanExecute = nameof(CanUseTab))]
    private Task ReloadAsync() => ExecuteAsync(Command("reload", ActiveTabId), Loc.Get("browser_op_reload"));

    [RelayCommand(CanExecute = nameof(CanOpenInBrowser))]
    private async Task OpenInBrowserAsync()
    {
        if (!CanOpenInBrowser || !Uri.TryCreate(Url, UriKind.Absolute, out var uri))
        {
            return;
        }
        var version = _bindingVersion;
        Banner = null;
        try
        {
            if (!await _uriLauncher(uri))
            {
                _scheduler.Post(() =>
                {
                    if (!_disposed && _bindingVersion == version)
                    {
                        Banner = Loc.Get("browser_open_in_browser_failed");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _scheduler.Post(() =>
            {
                if (!_disposed && _bindingVersion == version)
                {
                    Banner = Loc.Get("browser_op_failed", Loc.Get("browser_open_in_browser"), ex.Message);
                }
            });
        }
    }

    private static BrowserCommandAction Command(string command, string? tabId) =>
        new(command, EmptyInput) { TabId = tabId, UserInitiated = true };

    private static Task<bool> LaunchWithSystemBrowser(Uri uri)
    {
        using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.FromResult(process is not null);
    }

    private async Task ExecuteAsync(BrowserAction action, string operation)
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
            var result = await _controller.ExecuteAsync(sessionId, action, CancellationToken.None);
            _scheduler.Post(() =>
            {
                if (!_disposed && _bindingVersion == version)
                {
                    Refresh();
                    if (result is { Ok: false })
                    {
                        Banner = result.Error ?? Loc.Get("browser_op_failed_plain", operation);
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
                    Banner = Loc.Get("browser_op_failed", operation, ex.Message);
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
        StateText = status?.State switch
        {
            null or BrowserSessionState.Idle => Loc.Get("browser_state_idle"),
            BrowserSessionState.Launched => Loc.Get("browser_state_launched"),
            BrowserSessionState.Busy => Loc.Get("browser_state_busy"),
            BrowserSessionState.Failed => Loc.Get("status_failed"),
            _ => status!.State.ToString(),
        };
        Url = SelectedTab?.Url ?? status?.Url;
        Title = SelectedTab?.Title ?? status?.Title;
        UpdatedText = status is null ? string.Empty : RelativeTime.Format(status.UpdatedAt.ToUniversalTime());
        UpdatePresenter();
        Banner = status?.Error ?? _presenterError;
        NotifyCommandsChanged();
        ClearScreenshot();
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
                _presenterError = Loc.Get("browser_presenter_failed", ex.Message);
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

    private void ClearScreenshot()
    {
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
