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
    private string _updatedText = string.Empty;

    [ObservableProperty]
    private IImage? _screenshot;

    public bool HasScreenshot => Screenshot is not null;

    public bool CanInteract => ActiveSessionId is not null;

    public void Bind(SessionId? sessionId) => ActiveSessionId = sessionId;

    partial void OnScreenshotChanged(IImage? value) => OnPropertyChanged(nameof(HasScreenshot));

    partial void OnActiveSessionIdChanged(SessionId? value)
    {
        OnPropertyChanged(nameof(CanInteract));
        Refresh();
    }

    [RelayCommand]
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
        _scheduler.Post(() => ApplyStatus(e.Status));
    }

    private void ApplyStatus(BrowserSessionStatus? status)
    {
        StateText = status?.State.ToString() ?? nameof(BrowserSessionState.Idle);
        Url = status?.Url;
        Title = status?.Title;
        UpdatedText = status is null ? string.Empty : RelativeTime.Format(status.UpdatedAt.ToUniversalTime());
        if (status?.LatestScreenshotUri is { } uri
            && !string.Equals(uri, _loadedScreenshotUri, StringComparison.OrdinalIgnoreCase))
        {
            LoadScreenshot(uri);
        }
    }

    private void LoadScreenshot(string uri)
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
        var previous = Screenshot;
        Screenshot = loaded;
        (previous as IDisposable)?.Dispose();
    }

    public void Dispose() => _controller.StatusChanged -= OnControllerStatusChanged;
}
