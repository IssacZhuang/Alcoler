using Alcoler.Contracts.Browser;
using Alcoler.Contracts.Identifiers;
using Alcoler.Workbench.Ui;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Alcoler.Workbench.ViewModel;

/// <summary>
/// Status surface for the independent headed browser window of the active session.
/// Holds no agent state: it only mirrors the controller's status events and loads
/// the latest screenshot. Image construction is injectable so headless and test
/// hosts can skip it entirely.
/// </summary>
public sealed partial class BrowserPaneViewModel : ObservableObject, IDisposable
{
    private readonly IBrowserController _controller;
    private readonly IUiScheduler _scheduler;
    private readonly Func<string, IImage?>? _imageLoader;
    private string? _loadedScreenshotUri;
    private DateTimeOffset _loadedScreenshotUpdatedAt;
    private bool _disposed;

    public BrowserPaneViewModel(
        IBrowserController controller,
        IUiScheduler scheduler,
        Func<string, IImage?>? imageLoader = null)
    {
        _controller = controller;
        _scheduler = scheduler;
        _imageLoader = imageLoader;
        _controller.StatusChanged += OnControllerStatusChanged;
    }

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
    [NotifyCanExecuteChangedFor(nameof(NavigateCommand))]
    private bool _isNavigating;

    [ObservableProperty]
    private string? _banner;

    [ObservableProperty]
    private string _updatedText = string.Empty;

    [ObservableProperty]
    private IImage? _screenshot;

    public bool HasScreenshot => Screenshot is not null;

    public bool CanInteract => ActiveSessionId is not null;

    public void Bind(SessionId? sessionId) => ActiveSessionId = sessionId;

    partial void OnScreenshotChanged(IImage? value) => OnPropertyChanged(nameof(HasScreenshot));

    partial void OnUrlChanged(string? value) => Address = value ?? string.Empty;

    partial void OnActiveSessionIdChanged(SessionId? value)
    {
        OnPropertyChanged(nameof(CanInteract));
        TakeOverCommand.NotifyCanExecuteChanged();
        NavigateCommand.NotifyCanExecuteChanged();
        Banner = null;
        Url = null;
        Address = string.Empty;
        ClearScreenshot();
        Refresh();
    }

    private bool CanNavigate => CanInteract && !IsNavigating && !string.IsNullOrWhiteSpace(Address);

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private async Task NavigateAsync()
    {
        if (!CanNavigate || ActiveSessionId is not { } sessionId)
        {
            return;
        }
        var address = Address.Trim();
        if (!address.Contains("://", StringComparison.Ordinal)
            && !address.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            address = "https://" + address;
        }
        Banner = null;
        IsNavigating = true;
        try
        {
            var result = await _controller.ExecuteAsync(
                sessionId, new BrowserNavigateAction(address), CancellationToken.None);
            if (!_disposed && ActiveSessionId == sessionId)
            {
                Refresh();
                Banner = result.Ok ? null : result.Error ?? "Navigation failed.";
            }
        }
        catch (Exception ex)
        {
            if (!_disposed && ActiveSessionId == sessionId)
            {
                Banner = $"Navigation failed: {ex.Message}";
            }
        }
        finally
        {
            IsNavigating = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task TakeOverAsync()
    {
        if (ActiveSessionId is { } sessionId)
        {
            await _controller.TakeoverAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private void Refresh() => ApplyStatus(
        ActiveSessionId is { } sessionId ? _controller.GetStatus(sessionId) : null);

    private void OnControllerStatusChanged(object? sender, BrowserStatusChangedEventArgs e)
    {
        if (ActiveSessionId is not { } sessionId || e.SessionId != sessionId)
        {
            return;
        }
        _scheduler.Post(() =>
        {
            if (ActiveSessionId == e.SessionId)
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
        StateText = status?.State.ToString() ?? nameof(BrowserSessionState.Idle);
        Url = status?.Url;
        Title = status?.Title;
        UpdatedText = status is null ? string.Empty : RelativeTime.Format(status.UpdatedAt.ToUniversalTime());
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
        _controller.StatusChanged -= OnControllerStatusChanged;
        ClearScreenshot();
    }
}
