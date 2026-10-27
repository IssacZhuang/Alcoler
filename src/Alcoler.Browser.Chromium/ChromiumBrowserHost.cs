using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Alcoler.Browser;
using Alcoler.Contracts.Identifiers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alcoler.Browser.Chromium;

public sealed class ChromiumBrowserOptions
{
    public string? ExecutablePath { get; init; }
    public bool? Headless { get; init; }
    public string? BinaryCacheRoot { get; init; }

    // Headless is the operating mode: a visible browser window must never appear unless
    // explicitly requested for diagnostics.
    public bool EffectiveHeadless => Headless ?? true;
}

public sealed class ChromiumBrowserHost : IBrowserHost
{
    private readonly string _dataRoot;
    private readonly ChromiumBrowserOptions _options;
    private readonly ILogger _logger;
    private readonly string _binaryCacheRoot;
    private readonly Func<CancellationToken, Task<string>> _binaryResolver;
    private readonly Func<string, string, bool, IChromiumProcess> _processFactory;
    private readonly Func<ICdpWire> _wireFactory;
    private readonly Lock _sync = new();
    private readonly Dictionary<SessionId, SessionEntry> _sessions = [];
    private bool _disposed;

    public event EventHandler<BrowserNewTabRequestedEventArgs>? NewTabRequested;

    public ChromiumBrowserHost(string dataRoot, ChromiumBrowserOptions? options = null, ILogger<ChromiumBrowserHost>? logger = null,
        Func<CancellationToken, Task<string>>? binaryResolver = null,
        Func<string, string, bool, IChromiumProcess>? processFactory = null,
        Func<ICdpWire>? wireFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = Path.GetFullPath(dataRoot);
        _options = options ?? new();
        _logger = logger ?? NullLogger<ChromiumBrowserHost>.Instance;
        _binaryCacheRoot = string.IsNullOrWhiteSpace(_options.BinaryCacheRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alcoler", "browser-runtime")
            : Path.GetFullPath(_options.BinaryCacheRoot);
        _binaryResolver = binaryResolver
            ?? (ct => new ChromiumBinary(_binaryCacheRoot).ResolveAsync(_options.ExecutablePath, ct));
        _processFactory = processFactory
            ?? ((executablePath, profileDirectory, headless) => new ChromiumProcess(executablePath, profileDirectory, headless));
        _wireFactory = wireFactory ?? (() => new WebSocketCdpWire());
    }

    public async Task<IBrowserPage> CreatePageAsync(SessionId sessionId, string tabId, CancellationToken cancellationToken, string? adoptTargetId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        while (true)
        {
            var entry = GetOrCreateEntry(sessionId);
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (entry.Dead) continue;
                var connection = entry.Connection ?? await InitializeSessionAsync(entry, cancellationToken).ConfigureAwait(false);
                if (entry.PagesByTabId.TryGetValue(tabId, out var existing)) return existing;
                var targetId = await ResolveTargetIdAsync(entry, adoptTargetId, cancellationToken).ConfigureAwait(false);
                var attached = await connection.SendAsync("Target.attachToTarget",
                    D(("targetId", targetId), ("flatten", true)), cancellationToken).ConfigureAwait(false);
                var pageSessionId = attached.GetProperty("sessionId").GetString()
                    ?? throw new InvalidOperationException("The browser did not return a session for the new target.");
                if (!entry.DownloadsConfigured)
                {
                    await connection.SendAsync("Browser.setDownloadBehavior",
                        D(("behavior", "allow"), ("downloadPath", entry.DownloadsDirectory), ("eventsEnabled", true)),
                        cancellationToken).ConfigureAwait(false);
                    entry.DownloadsConfigured = true;
                }
                var page = new ChromiumBrowserPage(connection, tabId, targetId, pageSessionId);
                lock (_sync)
                {
                    entry.PagesByTabId[tabId] = page;
                    entry.PagesByTargetId[targetId] = page;
                }
                return page;
            }
            finally
            {
                entry.Gate.Release();
            }
        }
    }

    public async Task ClosePageAsync(string tabId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var found = FindPage(tabId);
            if (found is null) return;
            var (sessionId, entry, page) = found.Value;
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (entry.Dead) continue;
                if (!entry.PagesByTabId.Remove(tabId)) return;
                lock (_sync)
                {
                    if (entry.PagesByTargetId.TryGetValue(page.TargetId, out var registered) && ReferenceEquals(registered, page))
                    {
                        entry.PagesByTargetId.Remove(page.TargetId);
                    }
                }
                page.Close();
                page.Dispose();
                await TrySendAsync(entry, "Target.closeTarget", D("targetId", page.TargetId)).ConfigureAwait(false);
                if (entry.PagesByTabId.Count == 0) await TearDownSessionAsync(sessionId, entry).ConfigureAwait(false);
                return;
            }
            finally
            {
                entry.Gate.Release();
            }
        }
    }

    public async Task ShowPageAsync(string tabId, CancellationToken cancellationToken)
    {
        var (_, entry, page) = RequirePage(tabId);
        if (entry.Headless) throw new InvalidOperationException("Showing a browser page is not available in headless mode.");
        await page.SendAsync("Page.bringToFront", null, cancellationToken).ConfigureAwait(false);
        await SetWindowStateAsync(entry, page, "normal", cancellationToken).ConfigureAwait(false);
    }

    public async Task HidePageAsync(string tabId, CancellationToken cancellationToken)
    {
        var (_, entry, page) = RequirePage(tabId);
        if (entry.Headless) throw new InvalidOperationException("Hiding a browser page is not available in headless mode.");
        await SetWindowStateAsync(entry, page, "minimized", cancellationToken).ConfigureAwait(false);
    }

    private async Task SetWindowStateAsync(SessionEntry entry, ChromiumBrowserPage page, string windowState, CancellationToken cancellationToken)
    {
        var connection = entry.Connection ?? throw new InvalidOperationException("The browser session is no longer connected.");
        var window = await connection.SendAsync("Browser.getWindowForTarget",
            D("targetId", page.TargetId), cancellationToken).ConfigureAwait(false);
        var windowId = window.GetProperty("windowId").GetInt64();
        await connection.SendAsync("Browser.setWindowBounds",
            D(("windowId", windowId), ("bounds", D("windowState", windowState))), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ResolveTargetIdAsync(SessionEntry entry, string? adoptTargetId, CancellationToken cancellationToken)
    {
        var connection = entry.Connection ?? throw new InvalidOperationException("The browser session is no longer connected.");
        if (adoptTargetId is not null && entry.Popups.Remove(adoptTargetId, out _)) return adoptTargetId;
        var created = await connection.SendAsync("Target.createTarget",
            D(("url", "about:blank"), ("background", true)), cancellationToken).ConfigureAwait(false);
        return created.GetProperty("targetId").GetString()
            ?? throw new InvalidOperationException("The browser did not return a target for a new tab.");
    }

    private SessionEntry GetOrCreateEntry(SessionId sessionId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_sessions.TryGetValue(sessionId, out var entry))
            {
                entry = new SessionEntry
                {
                    SessionId = sessionId,
                    ProfileDirectory = Path.Combine(_dataRoot, "chromium-profiles", Sha256Hex(sessionId.Value)),
                    Headless = _options.EffectiveHeadless,
                };
                _sessions.Add(sessionId, entry);
            }
            return entry;
        }
    }

    private (SessionId SessionId, SessionEntry Entry, ChromiumBrowserPage Page)? FindPage(string tabId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var (sessionId, entry) in _sessions)
            {
                if (entry.PagesByTabId.TryGetValue(tabId, out var page)) return (sessionId, entry, page);
            }
        }
        return null;
    }

    private (SessionId, SessionEntry, ChromiumBrowserPage) RequirePage(string tabId) =>
        FindPage(tabId) ?? throw new KeyNotFoundException($"Browser tab '{tabId}' does not exist.");

    // A fresh launch against a profile whose predecessor is still shutting down defers to it and dies before the
    // DevTools socket accepts; bounded retries absorb that handoff race.
    private async Task<CdpConnection> InitializeSessionAsync(SessionEntry entry, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await StartSessionProcessAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < 5 && ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Browser launch attempt {Attempt} for session {Session} failed; retrying", attempt, entry.SessionId);
                await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<CdpConnection> StartSessionProcessAsync(SessionEntry entry, CancellationToken cancellationToken)
    {
        var executable = await _binaryResolver(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(entry.ProfileDirectory);
        var process = _processFactory(executable, entry.ProfileDirectory, entry.Headless);
        process.Exited += (_, _) => OnProcessExited(entry);
        await process.StartAsync(cancellationToken).ConfigureAwait(false);
        entry.Process = process;
        var connection = new CdpConnection();
        connection.BrowserEvent += protocolEvent => EnqueueTargetEvent(entry, protocolEvent);
        try
        {
            await connection.ConnectAsync(_wireFactory(), process.WsUri, cancellationToken).ConfigureAwait(false);
            await connection.SendAsync("Target.setDiscoverTargets", D("discover", true), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await process.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
            process.Dispose();
            connection.Dispose();
            throw;
        }
        entry.Connection = connection;
        entry.EventPump = Task.Run(() => PumpEventsAsync(entry));
        return connection;
    }

    private void EnqueueTargetEvent(SessionEntry entry, BrowserProtocolEvent protocolEvent)
    {
        if (protocolEvent.Method is not ("Target.targetCreated" or "Target.targetInfoChanged" or "Target.targetDestroyed")) return;
        entry.Events.Writer.TryWrite(protocolEvent);
    }

    // Target events must be applied under the session gate in wire order; dispatching them inline on the receive
    // loop could deadlock against a CreatePageAsync holding the gate while awaiting its own response.
    private async Task PumpEventsAsync(SessionEntry entry)
    {
        try
        {
            await foreach (var protocolEvent in entry.Events.Reader.ReadAllAsync(entry.Lifetime.Token).ConfigureAwait(false))
            {
                await entry.Gate.WaitAsync(entry.Lifetime.Token).ConfigureAwait(false);
                try
                {
                    DispatchTargetEvent(entry, protocolEvent);
                }
                finally
                {
                    entry.Gate.Release();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The browser target event pump stopped unexpectedly");
        }
    }

    private void DispatchTargetEvent(SessionEntry entry, BrowserProtocolEvent protocolEvent)
    {
        if (entry.Dead) return;
        var parameters = protocolEvent.Parameters;
        switch (protocolEvent.Method)
        {
            case "Target.targetCreated":
            {
                if (!parameters.TryGetProperty("targetInfo", out var info) || info.ValueKind != JsonValueKind.Object) return;
                if (String(info, "type") != "page") return;
                var targetId = String(info, "targetId");
                if (targetId is null || entry.PagesByTargetId.ContainsKey(targetId)) return;
                var url = String(info, "url") ?? "about:blank";
                var openerId = String(info, "openerId");
                if (openerId is not null && entry.PagesByTargetId.TryGetValue(openerId, out var opener))
                {
                    // Chrome announces popups with an empty URL; the notification waits for a committed navigation.
                    RegisterPendingTarget(entry, targetId, url, opener.Id, userInitiated: false);
                    return;
                }
                ChromiumBrowserPage? anchor = null;
                foreach (var page in entry.PagesByTargetId.Values)
                {
                    anchor = page;
                    break;
                }
                if (anchor is not null && openerId is null)
                {
                    RegisterPendingTarget(entry, targetId, url, anchor.Id, userInitiated: true);
                    return;
                }
                _ = TrySendAsync(entry, "Target.closeTarget", D("targetId", targetId));
                return;
            }
            case "Target.targetInfoChanged":
            {
                if (!parameters.TryGetProperty("targetInfo", out var info) || info.ValueKind != JsonValueKind.Object) return;
                var targetId = String(info, "targetId");
                if (targetId is not null && entry.PagesByTargetId.TryGetValue(targetId, out var page))
                {
                    page.OnTargetInfoChanged(String(info, "title"), String(info, "url"));
                    return;
                }
                if (targetId is not null && entry.Popups.TryGetValue(targetId, out var pending) && !pending.Notified)
                {
                    var url = String(info, "url");
                    if (url is not null && IsNavigableUrl(url))
                        RegisterPendingTarget(entry, targetId, url, pending.OpenerTabId, pending.UserInitiated);
                }
                return;
            }
            case "Target.targetDestroyed":
            {
                var targetId = String(parameters, "targetId");
                if (targetId is null) return;
                lock (_sync) entry.Popups.Remove(targetId);
                // The page stays registered: the controller answers Host.closed with ClosePageAsync, which performs
                // the unregister and, for the last tab, the session teardown.
                if (entry.PagesByTargetId.TryGetValue(targetId, out var page)) page.Close();
                return;
            }
        }
    }

    private async Task TrySendAsync(SessionEntry entry, string method, object? parameters)
    {
        if (entry.Connection is null) return;
        try
        {
            using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await entry.Connection.SendAsync(method, parameters, bounded.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Best-effort CDP command {Method} failed", method);
        }
        catch (OperationCanceledException) { }
    }

    private void OnProcessExited(SessionEntry entry)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await entry.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (entry.Dead) return;
                    await TearDownSessionAsync(entry.SessionId, entry).ConfigureAwait(false);
                }
                finally
                {
                    entry.Gate.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Browser process exit cleanup failed");
            }
        });
    }

    private async Task TearDownSessionAsync(SessionId sessionId, SessionEntry entry)
    {
        // Gate held by the caller.
        entry.Dead = true;
        lock (_sync)
        {
            if (_sessions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, entry)) _sessions.Remove(sessionId);
        }
        entry.Lifetime.Cancel();
        entry.Events.Writer.TryComplete();
        foreach (var page in entry.PagesByTabId.Values.ToArray())
        {
            page.Close();
            page.Dispose();
        }
        lock (_sync)
        {
            entry.PagesByTabId.Clear();
            entry.PagesByTargetId.Clear();
            entry.Popups.Clear();
        }
        await TrySendAsync(entry, "Browser.close", null).ConfigureAwait(false);
        if (entry.Process is not null)
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await entry.Process.ShutdownAsync(grace.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Browser process shutdown failed");
            }
        }
        entry.Connection?.Dispose();
        entry.Process?.Dispose();
    }

    public void Dispose()
    {
        SessionEntry[] entries;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            entries = [.. _sessions.Values];
            _sessions.Clear();
        }
        try
        {
            Task.WaitAll(entries.Select(async entry =>
            {
                await entry.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try { await TearDownSessionAsync(entry.SessionId, entry).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogDebug(ex, "Browser session teardown failed during dispose"); }
                finally { entry.Gate.Release(); }
            }).ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disposing the Chromium browser host failed");
        }
    }

    private void RegisterPendingTarget(SessionEntry entry, string targetId, string url, string openerTabId, bool userInitiated)
    {
        var notify = IsNavigableUrl(url);
        lock (_sync) entry.Popups[targetId] = new PopupRequest(url, openerTabId, userInitiated, notify);
        if (notify) NewTabRequested?.Invoke(this, new(entry.SessionId, openerTabId, url, userInitiated, targetId));
    }

    private static bool IsNavigableUrl(string url) =>
        url.StartsWith("http://", StringComparison.Ordinal) || url.StartsWith("https://", StringComparison.Ordinal);

    private static string? String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static Dictionary<string, object?> D(string key, object? value) => new(1) { [key] = value };
    private static Dictionary<string, object?> D(params (string Key, object? Value)[] values)
    {
        var result = new Dictionary<string, object?>(values.Length);
        foreach (var (key, value) in values) result.Add(key, value);
        return result;
    }

    private sealed record PopupRequest(string Url, string OpenerTabId, bool UserInitiated, bool Notified);

    private sealed class SessionEntry
    {
        public required SessionId SessionId { get; init; }
        public required string ProfileDirectory { get; init; }
        public required bool Headless { get; init; }
        public string DownloadsDirectory => Path.Combine(ProfileDirectory, "downloads");
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationTokenSource Lifetime { get; } = new();
        public Channel<BrowserProtocolEvent> Events { get; } = Channel.CreateUnbounded<BrowserProtocolEvent>();
        public Dictionary<string, ChromiumBrowserPage> PagesByTabId { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ChromiumBrowserPage> PagesByTargetId { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PopupRequest> Popups { get; } = new(StringComparer.Ordinal);
        public CdpConnection? Connection;
        public IChromiumProcess? Process;
        public Task? EventPump;
        public bool DownloadsConfigured;
        public bool Dead;
    }
}
