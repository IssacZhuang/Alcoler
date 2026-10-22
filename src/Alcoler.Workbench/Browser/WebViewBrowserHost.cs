using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Alcoler.Browser;
using Alcoler.Contracts.Identifiers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alcoler.Workbench.Browser;

public sealed class BrowserNewTabRequestedEventArgs(SessionId sessionId, string openerTabId, string url, bool isUserInitiated = false) : EventArgs
{
    public SessionId SessionId { get; } = sessionId;
    public string OpenerTabId { get; } = openerTabId;
    public string Url { get; } = url;
    public bool IsUserInitiated { get; } = isUserInitiated;
}

public sealed class WebViewBrowserHost : IBrowserHost
{
    private readonly string _userDataFolder;
    private readonly Dictionary<string, WebViewBrowserPage> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BrowserPagePresenter> _presenters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Window> _userWindows = new(StringComparer.Ordinal);
    private Grid? _parkingDeck;
    private Window? _parkingWindow;
    private bool _disposed;

    public WebViewBrowserHost(string userDataFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataFolder);
        _userDataFolder = Path.GetFullPath(userDataFolder);
    }

    public event EventHandler? PagesChanged;
    public event EventHandler<BrowserNewTabRequestedEventArgs>? NewTabRequested;

    public async Task<IBrowserPage> CreatePageAsync(SessionId sessionId, string tabId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        WebViewBrowserPage page = await OnUiAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pages.TryGetValue(tabId, out var existing))
            {
                if (existing.SessionId != sessionId)
                {
                    throw new InvalidOperationException("A browser tab cannot change session ownership.");
                }
                return existing;
            }
            EnsureParkingWindow();
            var profileName = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.Value)));
            var created = new WebViewBrowserPage(this, sessionId, tabId, _userDataFolder, profileName);
            _pages.Add(tabId, created);
            _parkingDeck!.Children.Add(created.WebView);
            return created;
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            await page.Ready.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
            await OnUiAsync(() => PagesChanged?.Invoke(this, EventArgs.Empty), cancellationToken).ConfigureAwait(false);
            return page;
        }
        catch
        {
            await OnUiAsync(() => ClosePageCore(tabId), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task ClosePageAsync(string tabId, CancellationToken cancellationToken) =>
        OnUiAsync(() => ClosePageCore(tabId), cancellationToken);

    public Task HidePageAsync(string tabId, CancellationToken cancellationToken) => OnUiAsync(() =>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pages.TryGetValue(tabId, out var page))
        {
            ParkPage(page);
        }
        if (_userWindows.Remove(tabId, out var window))
        {
            window.Close();
        }
    }, cancellationToken);

    public async Task ShowPageAsync(string tabId, CancellationToken cancellationToken)
    {
        await OnUiAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _ = GetPage(tabId);
            if (_presenters.TryGetValue(tabId, out var attached) && attached.IsEffectivelyVisible)
            {
                if (TopLevel.GetTopLevel(attached) is Window parent)
                {
                    parent.Activate();
                }
                return;
            }
            if (!_userWindows.TryGetValue(tabId, out var window))
            {
                window = new Window
                {
                    Title = "Alcoler Browser",
                    Width = 1100,
                    Height = 800,
                    Content = CreatePagePresenter(tabId)
                };
                _userWindows.Add(tabId, window);
                window.Closed += (_, _) => _userWindows.Remove(tabId);
                window.Show();
            }
            else
            {
                window.Show();
            }
            window.Activate();
        }, cancellationToken).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_disposed && _pages.TryGetValue(tabId, out var page)
                && _presenters.TryGetValue(tabId, out var presenter) && presenter.IsEffectivelyVisible
                && ReferenceEquals(page.WebView.Parent, presenter))
            {
                page.WebView.Focus();
            }
        }, DispatcherPriority.Loaded, cancellationToken);
    }

    public Control? GetPageControl(string tabId)
    {
        Dispatcher.UIThread.VerifyAccess();
        return _pages.GetValueOrDefault(tabId)?.WebView;
    }

    public Control CreatePagePresenter(string tabId)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        GetPage(tabId);
        return new BrowserPagePresenter(this, tabId);
    }

    internal void AttachPresenter(BrowserPagePresenter presenter)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed || !presenter.IsEffectivelyVisible || !_pages.TryGetValue(presenter.TabId, out var page))
        {
            return;
        }
        if (!page.Ready.IsCompletedSuccessfully)
        {
            return;
        }
        MovePage(page, presenter);
        _presenters[presenter.TabId] = presenter;
    }

    internal void DetachPresenter(BrowserPagePresenter presenter)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_presenters.TryGetValue(presenter.TabId, out var active) && ReferenceEquals(active, presenter))
        {
            _presenters.Remove(presenter.TabId);
            if (!_disposed && _pages.TryGetValue(presenter.TabId, out var page))
            {
                ParkPage(page);
            }
        }
    }

    internal void RaiseNewTabRequested(WebViewBrowserPage page, string url, bool isUserInitiated) =>
        NewTabRequested?.Invoke(this, new BrowserNewTabRequestedEventArgs(page.SessionId, page.Id, url, isUserInitiated));

    internal void DisposePage(WebViewBrowserPage page)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ClosePageCore(page.Id);
        }
        else
        {
            Dispatcher.UIThread.Post(() => ClosePageCore(page.Id));
        }
    }

    internal static bool IsAllowedUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" or "about" or "data";

    private WebViewBrowserPage GetPage(string tabId) => _pages.TryGetValue(tabId, out var page)
        ? page : throw new KeyNotFoundException($"Browser tab '{tabId}' does not exist.");

    private void EnsureParkingWindow()
    {
        if (_parkingWindow is not null)
        {
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The native browser host currently requires Windows and Microsoft Edge WebView2 Runtime.");
        }
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("The native WebView2 host requires an STA Avalonia dispatcher.");
        }
        var runtime = WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebView2);
        if (!runtime.IsInstalled)
        {
            throw new InvalidOperationException("Microsoft Edge WebView2 Runtime is not installed. Install the Evergreen WebView2 Runtime to enable the browser.");
        }
        Directory.CreateDirectory(_userDataFolder);
        _parkingDeck = new Grid { Width = 1280, Height = 800 };
        _parkingWindow = new Window
        {
            Title = "Alcoler Browser Parking",
            Width = 1280,
            Height = 800,
            Opacity = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
            CanResize = false,
            SystemDecorations = SystemDecorations.None,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = _parkingDeck
        };
        _parkingWindow.Show();
        // Managed visibility must remain true for WebView layout and native attachment.
        var handle = _parkingWindow.TryGetPlatformHandle()?.Handle
            ?? throw new InvalidOperationException("The browser parking window has no native handle.");
        ShowWindow(handle, 0);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    private void ParkPage(WebViewBrowserPage page)
    {
        _presenters.Remove(page.Id);
        MovePage(page, _parkingDeck!);
    }

    private static void MovePage(WebViewBrowserPage page, Control destination)
    {
        var view = page.WebView;
        if (ReferenceEquals(view.Parent, destination))
        {
            return;
        }
        var previousRoot = TopLevel.GetTopLevel(view);
        view.UpdateLayout();
        // The destination must already have a live TopLevel when this scope ends.
        using (view.BeginReparenting(false))
        {
            RemoveFromParent(view);
            // Logical detachment queues layout on the old root; drain before changing roots.
            previousRoot?.UpdateLayout();
            if (destination is Panel panel)
            {
                panel.Children.Add(view);
            }
            else if (destination is ContentControl content)
            {
                content.Content = view;
            }
        }
    }

    private static void RemoveFromParent(Control view)
    {
        if (view.Parent is Panel panel)
        {
            panel.Children.Remove(view);
        }
        else if (view.Parent is ContentControl content)
        {
            content.Content = null;
        }
    }

    private void ClosePageCore(string tabId)
    {
        if (!_pages.Remove(tabId, out var page))
        {
            return;
        }
        _presenters.Remove(tabId);
        page.DisposeNative();
        RemoveFromParent(page.WebView);
        if (_userWindows.Remove(tabId, out var window))
        {
            window.Close();
        }
        PagesChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static async Task OnUiAsync(Action action, CancellationToken cancellationToken)
    {
        await OnUiAsync(() => { action(); return true; }, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<T> OnUiAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            return action();
        }
        return await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken);
    }

    public void Dispose()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Dispose);
            return;
        }
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var id in _pages.Keys.ToArray())
        {
            ClosePageCore(id);
        }
        _parkingWindow?.Close();
        _parkingWindow = null;
        _parkingDeck = null;
    }
}

internal sealed class BrowserPagePresenter(WebViewBrowserHost host, string tabId) : Panel, IDisposable
{
    private bool _disposed;
    private bool _attachQueued;
    private readonly List<Visual> _ancestors = [];

    public string TabId { get; } = tabId;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        for (var ancestor = this.GetVisualParent(); ancestor is not null; ancestor = ancestor.GetVisualParent())
        {
            ancestor.PropertyChanged += OnAncestorPropertyChanged;
            _ancestors.Add(ancestor);
        }
        UpdateVisibility();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnsubscribeAncestors();
        host.DetachPresenter(this);
        base.OnDetachedFromVisualTree(e);
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty)
        {
            UpdateVisibility();
            Dispatcher.UIThread.Post(UpdateVisibility, DispatcherPriority.Loaded);
        }
    }

    private void UnsubscribeAncestors()
    {
        foreach (var ancestor in _ancestors)
        {
            ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        }
        _ancestors.Clear();
    }

    private void UpdateVisibility()
    {
        if (_disposed)
        {
            return;
        }
        if (IsEffectivelyVisible && TopLevel.GetTopLevel(this) is not null)
        {
            if (!_attachQueued)
            {
                _attachQueued = true;
                Dispatcher.UIThread.Post(() =>
                {
                    _attachQueued = false;
                    if (!_disposed && IsEffectivelyVisible && TopLevel.GetTopLevel(this) is not null)
                    {
                        host.AttachPresenter(this);
                    }
                }, DispatcherPriority.Loaded);
            }
        }
        else
        {
            host.DetachPresenter(this);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!_disposed && change.Property == IsVisibleProperty)
        {
            UpdateVisibility();
        }
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_disposed)
        {
            _disposed = true;
            UnsubscribeAncestors();
            host.DetachPresenter(this);
        }
    }
}
