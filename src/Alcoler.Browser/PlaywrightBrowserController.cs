using Alcoler.Contracts.Browser;
using Alcoler.Contracts.Identifiers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace Alcoler.Browser;

/// <summary>
/// Headed Chromium automation: one persistent-context browser process shared by all
/// sessions, one page per session. A persistent context cannot be multiplied per
/// session (Chromium locks the user-data-dir to a single process), so session
/// isolation is at page level inside that context.
/// </summary>
public sealed class PlaywrightBrowserController : IBrowserController
{
    private const int MaxPages = 8;
    private static readonly TimeSpan IdlePageLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ReclaimInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(60);
    private static readonly float ActionTimeoutMs = 30_000f;

    private readonly ILogger _logger;
    private readonly string _userDataDir;
    private readonly string _screenshotDir;
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<SessionId, SessionEntry> _sessions = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _reclaimLoop;

    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private bool _disposed;

    public PlaywrightBrowserController(ILogger<PlaywrightBrowserController>? logger = null)
        : this(
            (ILogger?)logger ?? NullLogger<PlaywrightBrowserController>.Instance,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Alcoler", "browser-profile"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Alcoler", "browser-screenshots"))
    {
    }

    internal PlaywrightBrowserController(ILogger logger, string userDataDir, string screenshotDir)
    {
        _logger = logger;
        _userDataDir = userDataDir;
        _screenshotDir = screenshotDir;
        _reclaimLoop = ReclaimIdlePagesAsync(new PeriodicTimer(ReclaimInterval), _shutdown.Token);
    }

    public event EventHandler<BrowserStatusChangedEventArgs>? StatusChanged;

    public BrowserSessionStatus? GetStatus(SessionId sessionId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(sessionId, out var entry) ? entry.Status : null;
        }
    }

    public async Task<BrowserActionResult> ExecuteAsync(SessionId sessionId, BrowserAction action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entry = GetOrCreateEntry(sessionId);
        using var inflight = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetInflight(entry, inflight);
            entry.LastTouched = DateTimeOffset.UtcNow;
            SetStatus(sessionId, entry, BrowserSessionState.Busy);
            var context = await GetContextAsync(inflight.Token).ConfigureAwait(false);
            var page = await GetOrCreatePageAsync(sessionId, entry, context, inflight.Token).ConfigureAwait(false);
            var result = await RunActionAsync(sessionId, entry, page, action, inflight.Token).ConfigureAwait(false);
            var title = await AwaitOrCancel(page.TitleAsync(), inflight.Token).ConfigureAwait(false);
            SetStatus(sessionId, entry, BrowserSessionState.Launched, page.Url, title);
            return result;
        }
        catch (OperationCanceledException) when (inflight.IsCancellationRequested)
        {
            SetStatus(sessionId, entry, entry.Page is { IsClosed: false } ? BrowserSessionState.Launched : BrowserSessionState.Idle);
            return new BrowserActionResult(Ok: false, Error: "Browser action canceled.");
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException or IOException)
        {
            _logger.LogWarning(ex, "Browser action {Verb} failed for session {SessionId}", action.Verb, sessionId);
            SetStatus(sessionId, entry, BrowserSessionState.Failed);
            return new BrowserActionResult(Ok: false, Error: ex.Message);
        }
        finally
        {
            SetInflight(entry, null);
            entry.Gate.Release();
        }
    }

    public Task CancelAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        CancellationTokenSource? inflight;
        lock (_gate)
        {
            inflight = _sessions.TryGetValue(sessionId, out var entry) ? entry.Inflight : null;
        }
        // Playwright calls take no cancellation tokens; canceling stops the wait
        // in ExecuteAsync and releases the session's busy state.
        try
        {
            inflight?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        return Task.CompletedTask;
    }

    public async Task TakeoverAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        IPage? page;
        lock (_gate)
        {
            page = _sessions.TryGetValue(sessionId, out var entry) && entry.Page is { IsClosed: false } existing
                ? existing
                : null;
        }
        if (page is null)
        {
            return;
        }
        try
        {
            await AwaitOrCancel(page.BringToFrontAsync(), cancellationToken).ConfigureAwait(false);
        }
        catch (PlaywrightException ex)
        {
            _logger.LogWarning(ex, "Bring-to-front failed for session {SessionId}", sessionId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _shutdown.Cancel();
        WaitForNoThrow(_reclaimLoop);
        lock (_gate)
        {
            foreach (var entry in _sessions.Values)
            {
                entry.Inflight?.Cancel();
                entry.Inflight?.Dispose();
                entry.Gate.Dispose();
            }
        }
        if (_context is { } context)
        {
            WaitForNoThrow(context.CloseAsync());
        }
        // Disposing the driver tears down the browser process even if CloseAsync timed out.
        _playwright?.Dispose();
        _launchGate.Dispose();
        _shutdown.Dispose();
    }

    private static void WaitForNoThrow(Task task)
    {
        try
        {
            task.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
    }

    private static async Task AwaitOrCancel(Task task, CancellationToken cancellationToken)
    {
        var abandonment = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (cancellationToken.Register(() => abandonment.TrySetResult()))
        {
            if (await Task.WhenAny(task, abandonment.Task).ConfigureAwait(false) == abandonment.Task)
            {
                ObserveLater(task);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        await task.ConfigureAwait(false);
    }

    private static async Task<T> AwaitOrCancel<T>(Task<T> task, CancellationToken cancellationToken)
    {
        var abandonment = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (cancellationToken.Register(() => abandonment.TrySetResult()))
        {
            if (await Task.WhenAny(task, abandonment.Task).ConfigureAwait(false) == abandonment.Task)
            {
                ObserveLater(task);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        return await task.ConfigureAwait(false);
    }

    // Abandoned Playwright tasks must not surface as unobserved exceptions.
    private static void ObserveLater(Task task) => _ = task.ContinueWith(
        static t => _ = t.Exception,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

    private async Task<BrowserActionResult> RunActionAsync(
        SessionId sessionId, SessionEntry entry, IPage page, BrowserAction action, CancellationToken ct)
    {
        switch (action)
        {
            case BrowserNavigateAction navigate:
            {
                var response = await AwaitOrCancel(
                    page.GotoAsync(navigate.Url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = ActionTimeoutMs,
                    }), ct).ConfigureAwait(false);
                return new BrowserActionResult(
                    Ok: true, Url: page.Url, Title: await PageTitleAsync(page, ct).ConfigureAwait(false),
                    Text: response is null ? null : $"HTTP {response.Status}");
            }
            case BrowserClickAction click:
                await AwaitOrCancel(
                    page.ClickAsync(click.Selector, new PageClickOptions { Timeout = ActionTimeoutMs }), ct)
                    .ConfigureAwait(false);
                return await OkWithUrlAndTitle(page, ct).ConfigureAwait(false);
            case BrowserTypeAction type:
                await AwaitOrCancel(
                    page.FillAsync(type.Selector, type.Text, new PageFillOptions { Timeout = ActionTimeoutMs }), ct)
                    .ConfigureAwait(false);
                if (type.Submit)
                {
                    await AwaitOrCancel(page.Keyboard.PressAsync("Enter"), ct).ConfigureAwait(false);
                }
                return await OkWithUrlAndTitle(page, ct).ConfigureAwait(false);
            case BrowserScreenshotAction:
            {
                var png = await AwaitOrCancel(
                    page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png }), ct)
                    .ConfigureAwait(false);
                PersistScreenshot(sessionId, entry, png);
                return new BrowserActionResult(
                    Ok: true, Url: page.Url, Title: await PageTitleAsync(page, ct).ConfigureAwait(false),
                    ScreenshotPng: png);
            }
            case BrowserPressAction press:
                await AwaitOrCancel(page.Keyboard.PressAsync(press.Key), ct).ConfigureAwait(false);
                return await OkWithUrlAndTitle(page, ct).ConfigureAwait(false);
            case BrowserScrollAction scroll:
                await AwaitOrCancel(page.Mouse.WheelAsync(scroll.DeltaX, scroll.DeltaY), ct).ConfigureAwait(false);
                return await OkWithUrlAndTitle(page, ct).ConfigureAwait(false);
            case BrowserWaitAction wait:
                await Task.Delay(wait.Milliseconds, ct).ConfigureAwait(false);
                return await OkWithUrlAndTitle(page, ct).ConfigureAwait(false);
            case BrowserReadTextAction:
                return new BrowserActionResult(
                    Ok: true, Url: page.Url, Title: await PageTitleAsync(page, ct).ConfigureAwait(false),
                    Text: await AwaitOrCancel(page.InnerTextAsync("body"), ct).ConfigureAwait(false));
            default:
                return new BrowserActionResult(Ok: false, Error: $"Unknown browser action '{action.Verb}'.");
        }
    }

    private static async Task<string> PageTitleAsync(IPage page, CancellationToken ct) =>
        await AwaitOrCancel(page.TitleAsync(), ct).ConfigureAwait(false);

    private async Task<BrowserActionResult> OkWithUrlAndTitle(IPage page, CancellationToken ct) =>
        new(Ok: true, Url: page.Url, Title: await PageTitleAsync(page, ct).ConfigureAwait(false));

    private void PersistScreenshot(SessionId sessionId, SessionEntry entry, byte[] png)
    {
        try
        {
            Directory.CreateDirectory(_screenshotDir);
            var path = Path.Combine(_screenshotDir, $"{sessionId.Value}.png");
            File.WriteAllBytes(path, png);
            lock (_gate)
            {
                entry.LatestScreenshotUri = path;
            }
        }
        catch (IOException ex)
        {
            // The in-memory copy still reaches the caller; only the pane loses the refresh.
            _logger.LogWarning(ex, "Persisting screenshot failed for session {SessionId}", sessionId);
        }
    }

    private SessionEntry GetOrCreateEntry(SessionId sessionId)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var existing))
            {
                return existing;
            }
            var entry = new SessionEntry();
            _sessions[sessionId] = entry;
            SetStatus(sessionId, entry, BrowserSessionState.Idle);
            return entry;
        }
    }

    private async Task<IBrowserContext> GetContextAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_context is { } current)
            {
                return current;
            }
        }
        await _launchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_context is { } current)
                {
                    return current;
                }
            }
            Directory.CreateDirectory(_userDataDir);
            Directory.CreateDirectory(_screenshotDir);
            _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
            var options = new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = false,
                ViewportSize = null,
            };
            IBrowserContext context;
            try
            {
                options.Channel = "msedge";
                context = await LaunchAsync(_playwright, _userDataDir, options, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                _logger.LogInformation(ex, "msedge channel unavailable, falling back to bundled chromium");
                options.Channel = null;
                context = await LaunchAsync(_playwright, _userDataDir, options, ct).ConfigureAwait(false);
            }
            context.Close += OnContextClosed;
            lock (_gate)
            {
                _context = context;
            }
            _logger.LogInformation("Browser launched with profile {UserDataDir}", _userDataDir);
            return context;
        }
        finally
        {
            _launchGate.Release();
        }
    }

    private static Task<IBrowserContext> LaunchAsync(
        IPlaywright playwright, string userDataDir,
        BrowserTypeLaunchPersistentContextOptions options, CancellationToken ct) =>
        playwright.Chromium.LaunchPersistentContextAsync(userDataDir, options).WaitAsync(LaunchTimeout, ct);

    private void OnContextClosed(object? sender, IBrowserContext context)
    {
        lock (_gate)
        {
            _context = null;
        }
        _logger.LogInformation("Browser closed");
    }

    private async Task<IPage> GetOrCreatePageAsync(
        SessionId sessionId, SessionEntry entry, IBrowserContext context, CancellationToken ct)
    {
        if (entry.Page is { IsClosed: false } page)
        {
            return page;
        }
        SessionEntry? victim = null;
        lock (_gate)
        {
            var livePages = _sessions.Values.Count(s => s.Page is { IsClosed: false });
            if (livePages >= MaxPages)
            {
                victim = _sessions.Values
                    .Where(s => s != entry && s.Page is { IsClosed: false })
                    .OrderBy(s => s.LastTouched)
                    .FirstOrDefault();
            }
        }
        if (victim?.Page is { } victimPage)
        {
            _logger.LogInformation("Page cap of {MaxPages} reached; evicting the least recently used page", MaxPages);
            lock (_gate)
            {
                victim.Page = null;
            }
            await AwaitOrCancel(victimPage.CloseAsync(), CancellationToken.None).ConfigureAwait(false);
        }
        var newPage = await AwaitOrCancel(context.NewPageAsync(), ct).ConfigureAwait(false);
        newPage.Close += (_, closedPage) =>
        {
            lock (_gate)
            {
                if (entry.Page == closedPage)
                {
                    entry.Page = null;
                }
            }
        };
        lock (_gate)
        {
            entry.Page = newPage;
        }
        return newPage;
    }

    private async Task ReclaimIdlePagesAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                List<IPage> stale = [];
                lock (_gate)
                {
                    var cutoff = DateTimeOffset.UtcNow - IdlePageLifetime;
                    foreach (var entry in _sessions.Values)
                    {
                        if (entry.Page is { IsClosed: false } page && entry.LastTouched < cutoff && entry.Inflight is null)
                        {
                            entry.Page = null;
                            stale.Add(page);
                        }
                    }
                }
                if (stale.Count > 0)
                {
                    _logger.LogInformation("Reclaimed {Count} idle browser pages", stale.Count);
                    await Task.WhenAll(stale.Select(p => p.CloseAsync())).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            timer.Dispose();
        }
    }

    private void SetInflight(SessionEntry entry, CancellationTokenSource? inflight)
    {
        lock (_gate)
        {
            entry.Inflight = inflight;
        }
    }

    private void SetStatus(
        SessionId sessionId, SessionEntry entry, BrowserSessionState state,
        string? url = null, string? title = null)
    {
        BrowserSessionStatus status;
        lock (_gate)
        {
            status = new BrowserSessionStatus(
                state,
                url ?? entry.Status.Url,
                title ?? entry.Status.Title,
                entry.LatestScreenshotUri,
                DateTimeOffset.UtcNow);
            entry.Status = status;
        }
        StatusChanged?.Invoke(this, new BrowserStatusChangedEventArgs(sessionId, status));
    }

    private sealed class SessionEntry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationTokenSource? Inflight { get; set; }
        public IPage? Page { get; set; }
        public BrowserSessionStatus Status { get; set; } = new(BrowserSessionState.Idle, UpdatedAt: DateTimeOffset.UtcNow);
        public string? LatestScreenshotUri { get; set; }
        public DateTimeOffset LastTouched { get; set; } = DateTimeOffset.UtcNow;
    }
}
