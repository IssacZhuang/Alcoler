using System.Text.Json;
using Alcoler.Browser.Automation;
using Alcoler.Contracts.Browser;
using Alcoler.Contracts.Identifiers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alcoler.Browser;

public sealed class BrowserController : IBrowserController
{
    private const int MaxTabs = 32;
    private readonly IBrowserHost _host;
    private readonly ILogger _logger;
    private readonly string _backendName;
    private readonly string? _capabilitiesText;
    private readonly Lock _sync = new();
    private readonly Dictionary<SessionId, SessionEntry> _sessions = [];
    private readonly CancellationTokenSource _shutdown = new();
    private int _reservedTabs;
    private bool _disposed;

    public BrowserController(IBrowserHost host, ILogger<BrowserController>? logger = null,
        string backendName = "webview2", string? capabilitiesText = null)
    {
        _host = host;
        _logger = logger ?? NullLogger<BrowserController>.Instance;
        _backendName = backendName;
        _capabilitiesText = capabilitiesText;
    }

    public event EventHandler<BrowserStatusChangedEventArgs>? StatusChanged;

    public BrowserSessionStatus? GetStatus(SessionId sessionId)
    {
        lock (_sync)
        {
            return _sessions.TryGetValue(sessionId, out var entry) ? entry.Status : null;
        }
    }

    public async Task<BrowserActionResult> ExecuteAsync(
        SessionId sessionId, BrowserAction action, CancellationToken cancellationToken)
    {
        if (action.Verb == "takeover")
        {
            await TakeoverAsync(sessionId, cancellationToken).ConfigureAwait(false);
            lock (_sync)
                return TabList(GetSession(sessionId)) with { Text = "Control handed to the user. Automated input is paused until the user resumes it." };
        }
        SessionEntry session;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            session = GetSession(sessionId);
        }
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await session.Gate.WaitAsync(pending.Token).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                pending.Token.ThrowIfCancellationRequested();
                if (session.UserControlled && !action.UserInitiated && !IsObservation(action))
                    return Failure("Browser is under human control. The user must choose Resume agent before automated interaction can continue.");
                if (session.UserControlled && action.Verb == "record_status")
                    return new(true, StructuredOutput: BrowserJson.Serialize(new Dictionary<string, object?> { ["state"] = "cancelled" }));
                session.Inflight = pending;
                session.Busy = true;
                session.Error = null;
            }
            Publish(sessionId, session);
            var result = await RunAsync(sessionId, session, action, pending.Token).ConfigureAwait(false);
            lock (_sync)
            {
                session.Error = result.Ok ? null : result.Error;
            }
            return result;
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
            return Failure("Browser action canceled. A command already sent to the browser may have taken effect; inspect the page before retrying.", uncertain: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogWarning(ex, "Browser action {Action} failed for {SessionId}", action.Verb, sessionId);
            lock (_sync) session.Error = ex.Message;
            return Failure(ex.Message);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(session.Inflight, pending)) session.Inflight = null;
                session.Busy = false;
            }
            Publish(sessionId, session);
            session.Gate.Release();
        }
    }

    public Task CancelAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_sessions.TryGetValue(sessionId, out var session)) session.Inflight?.Cancel();
        }
        return Task.CompletedTask;
    }

    public async Task TakeoverAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionEntry session;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            session = GetSession(sessionId);
            session.UserControlled = true;
            session.ControlGeneration++;
            session.Inflight?.Cancel();
        }
        Publish(sessionId, session);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        await session.Gate.WaitAsync(pending.Token).ConfigureAwait(false);
        try
        {
            TabEntry[] tabs;
            lock (_sync)
            {
                session.UserControlled = true;
                tabs = session.Tabs.Values.ToArray();
            }
            foreach (var tab in tabs)
                await tab.Automation.SuspendAsync(pending.Token).ConfigureAwait(false);
            string? active;
            lock (_sync) active = session.ActiveTabId;
            // Showing the page is a best-effort handover affordance: a headless backend has
            // no window to raise, but control is still gated by UserControlled.
            if (active is not null)
            {
                try { await _host.ShowPageAsync(active, pending.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { }
            }
        }
        finally
        {
            session.Gate.Release();
            Publish(sessionId, session);
        }
    }

    public Task<BrowserActionResult> OpenPopupAsync(
        SessionId sessionId, string openerTabId, string url, CancellationToken cancellationToken,
        bool isUserInitiated = false, string? adoptTargetId = null)
    {
        PopupAction action;
        lock (_sync)
        {
            if (_disposed || !_sessions.TryGetValue(sessionId, out var session))
                return Task.FromResult(Failure("The popup opener is no longer available."));
            action = new PopupAction(openerTabId, url, session.ControlGeneration, adoptTargetId)
            {
                UserInitiated = isUserInitiated && session.UserControlled && session.Inflight is null,
            };
        }
        return ExecuteAsync(sessionId, action, cancellationToken);
    }

    private async Task<BrowserActionResult> RunAsync(
        SessionId sessionId, SessionEntry session, BrowserAction action, CancellationToken ct)
    {
        var input = action is BrowserCommandAction command ? command.Input : default;
        switch (action.Verb)
        {
            case "capabilities":
                return new(true, Text: _capabilitiesText ?? "Embedded WebView browser. Windows WebView2 runtime is required for automation. Tabs and control ownership are session-scoped; browser data persists in an application profile. Use snapshot to discover elements; refs expire on navigation. File upload and external Chrome attachment are not supported.",
                    StructuredOutput: BrowserJson.Serialize(new Dictionary<string, object?>
                    {
                        ["backend"] = _backendName, ["platform"] = PlatformName(), ["maxTabs"] = MaxTabs,
                        ["observations"] = new[] { "snapshot", "dom_snapshot", "screenshot", "element_info", "evaluate" },
                        ["interactions"] = new[] { "navigate", "click", "dblclick", "hover", "fill", "type", "press", "scroll", "drag", "check", "uncheck", "select_option" },
                        ["lifecycle"] = new[] { "tabs", "new_tab", "select_tab", "close_tab", "back", "forward", "reload", "takeover", "resume", "finalize" },
                        ["events"] = new[] { "wait_for", "dialog", "downloads", "download_media" },
                        ["media"] = new[] { "viewport", "visibility", "record_start", "record_status", "record_cancel" },
                        ["unsupported"] = new[] { "networkidle", "file_upload", "external_browser_attachment" },
                    }));
            case "tabs":
                return TabList(session);
            case "resume":
                if (!action.UserInitiated)
                    return Failure("Only the user can resume automated control from the browser pane.");
                lock (_sync)
                {
                    session.UserControlled = false;
                    session.ControlGeneration++;
                }
                return TabList(session) with { Text = "Automated browser control resumed." };
            case "finalize":
            {
                var finalized = FinalizeTabs(session, input);
                if (session.UserControlled)
                {
                    TabEntry[] tabs;
                    lock (_sync) tabs = session.Tabs.Values.ToArray();
                    foreach (var tab in tabs)
                        await tab.Automation.SuspendAsync(ct).ConfigureAwait(false);
                }
                return finalized;
            }
            case "popup":
            case "new_tab":
            {
                if (action is PopupAction popup)
                {
                    if (popup.ControlGeneration != session.ControlGeneration)
                        return Failure("The popup was discarded because browser control ownership changed.");
                    _ = RequireTab(session, popup.OpenerTabId);
                }
                var url = action is PopupAction requested ? requested.Url : GetString(input, "url") ?? "about:blank";
                ValidateUrl(url);
                var created = await CreateTabAsync(sessionId, session, ct, action is PopupAction adopt ? adopt.AdoptTargetId : null).ConfigureAwait(false);
                if (url != "about:blank")
                {
                    var navigated = await created.Automation.ExecuteAsync(new BrowserNavigateAction(url), ct).ConfigureAwait(false);
                    return navigated with { TabId = created.Page.Id, StructuredOutput = TabList(session).StructuredOutput };
                }
                return TabList(session) with { TabId = created.Page.Id };
            }
            case "close_tab":
            {
                var tab = RequireTab(session, action.TabId ?? GetString(input, "tabId"));
                await CloseTabAsync(session, tab, ct).ConfigureAwait(false);
                return TabList(session);
            }
            case "select_tab":
            {
                var tab = RequireTab(session, action.TabId ?? GetString(input, "tabId"));
                lock (_sync) session.ActiveTabId = tab.Page.Id;
                // A user tab click implies focus, but a headless backend has no window to raise.
                if (action.UserInitiated)
                {
                    try { await _host.ShowPageAsync(tab.Page.Id, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { }
                }
                return TabList(session) with { TabId = tab.Page.Id };
            }
        }
        var tabId = action.TabId ?? GetString(input, "tabId");
        TabEntry? current;
        lock (_sync)
        {
            current = tabId is not null ? RequireTab(session, tabId)
                : session.ActiveTabId is { } active && session.Tabs.TryGetValue(active, out var tab) ? tab : null;
        }
        if (current is null && session.UserControlled)
            return Failure("No active browser tab is available under human control. The user must open a tab or resume the agent.");
        current ??= await CreateTabAsync(sessionId, session, ct).ConfigureAwait(false);
        if (action.Verb == "visibility")
        {
            var visible = input.TryGetProperty("visible", out var flag) && flag.GetBoolean();
            if (visible) await _host.ShowPageAsync(current.Page.Id, ct).ConfigureAwait(false);
            else await _host.HidePageAsync(current.Page.Id, ct).ConfigureAwait(false);
            return new(true, Url: current.Automation.Url, Title: current.Automation.Title,
                Text: visible ? "Browser page shown." : "Browser page moved to the background.", TabId: current.Page.Id);
        }
        if (action is BrowserNavigateAction navigation) ValidateUrl(navigation.Url);
        var result = await current.Automation.ExecuteAsync(action, ct).ConfigureAwait(false);
        return result with { TabId = current.Page.Id };
    }

    private async Task<TabEntry> CreateTabAsync(SessionId sessionId, SessionEntry session, CancellationToken ct, string? adoptTargetId = null)
    {
        lock (_sync)
        {
            if (_reservedTabs >= MaxTabs)
                throw new InvalidOperationException($"The browser has reached its {MaxTabs}-tab limit. Close unused tabs before opening another.");
            _reservedTabs++;
        }
        var id = "tab_" + Guid.NewGuid().ToString("N");
        IBrowserPage? page = null;
        CdpBrowserAutomation? automation = null;
        try
        {
            page = await _host.CreatePageAsync(sessionId, id, ct, adoptTargetId).ConfigureAwait(false);
            automation = new CdpBrowserAutomation(page);
            await automation.InitializeAsync(ct).ConfigureAwait(false);
            var entry = new TabEntry(page, automation);
            EventHandler changed = (_, _) => Publish(sessionId, session);
            EventHandler downloadsChanged = (_, _) => PublishDownloadsCoalesced(sessionId, session);
            EventHandler<BrowserProtocolEvent> protocol = (_, e) => OnProtocolEvent(sessionId, session, entry, e);
            entry.StateChanged = changed;
            entry.DownloadsChanged = downloadsChanged;
            entry.ProtocolEvent = protocol;
            automation.StateChanged += changed;
            automation.DownloadsChanged += downloadsChanged;
            page.ProtocolEvent += protocol;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ct.ThrowIfCancellationRequested();
                session.Tabs.Add(id, entry);
                session.ActiveTabId = id;
            }
            Publish(sessionId, session);
            return entry;
        }
        catch
        {
            automation?.Dispose();
            page?.Dispose();
            try { await _host.ClosePageAsync(id, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Closing an uninitialized browser tab failed"); }
            lock (_sync) _reservedTabs--;
            throw;
        }
    }

    private async Task CloseTabAsync(SessionEntry session, TabEntry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!session.Tabs.Remove(entry.Page.Id)) return;
            _reservedTabs--;
            if (session.ActiveTabId == entry.Page.Id) session.ActiveTabId = session.Tabs.Keys.LastOrDefault();
        }
        Detach(entry);
        await _host.ClosePageAsync(entry.Page.Id, CancellationToken.None).ConfigureAwait(false);
    }

    private void OnProtocolEvent(SessionId sessionId, SessionEntry session, TabEntry entry, BrowserProtocolEvent e)
    {
        if (e.Method == "Host.loadingChanged")
        {
            lock (_sync)
                entry.Loading = e.Parameters.TryGetProperty("isLoading", out var loading) && loading.GetBoolean();
        }
        else if (e.Method == "Page.frameStartedLoading")
        {
            lock (_sync) entry.Loading = true;
        }
        else if (e.Method == "Page.frameStoppedLoading")
        {
            lock (_sync) entry.Loading = false;
        }
        else if (e.Method is not ("Host.closed" or "Inspector.detached" or "Inspector.targetCrashed"))
        {
            return;
        }
        if (e.Method is "Host.closed" or "Inspector.detached" or "Inspector.targetCrashed")
        {
            if (e.SessionId is not null) return;
            lock (_sync)
            {
                if (!session.Tabs.Remove(entry.Page.Id)) return;
                _reservedTabs--;
                if (session.ActiveTabId == entry.Page.Id) session.ActiveTabId = session.Tabs.Keys.LastOrDefault();
                session.Error = "The browser page was closed. Open a new tab to continue.";
            }
            Detach(entry);
            _ = Task.Run(() => CloseNativePageAsync(entry.Page.Id));
        }
        Publish(sessionId, session);
    }

    private async Task CloseNativePageAsync(string tabId)
    {
        try { await _host.ClosePageAsync(tabId, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Closing native browser tab {TabId} failed", tabId); }
    }

    private BrowserActionResult FinalizeTabs(SessionEntry session, JsonElement input)
    {
        var keep = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("keep", out var ids)
            ? ids.EnumerateArray().Select(id => id.GetString()!).ToArray() : [];
        lock (_sync)
        {
            foreach (var id in keep) _ = RequireTab(session, id);
            foreach (var id in keep) session.Tabs[id].Keep = true;
            if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("release", out var release) && release.GetBoolean())
            {
                session.UserControlled = true;
                session.ControlGeneration++;
            }
        }
        return TabList(session) with { Text = "Browser deliverables retained. Unlisted tabs were not closed." };
    }

    private BrowserActionResult TabList(SessionEntry session)
    {
        lock (_sync)
        {
            var tabs = new object?[session.Tabs.Count];
            var index = 0;
            foreach (var tab in session.Tabs.Values)
            {
                tabs[index++] = new Dictionary<string, object?>(6)
                {
                    ["id"] = tab.Page.Id, ["url"] = tab.Automation.Url ?? "about:blank",
                    ["title"] = tab.Automation.Title ?? string.Empty,
                    ["active"] = tab.Page.Id == session.ActiveTabId,
                    ["userControlled"] = session.UserControlled,
                    ["keep"] = tab.Keep,
                };
            }
            return new(true, TabId: session.ActiveTabId, StructuredOutput: BrowserJson.Serialize(
                new Dictionary<string, object?> { ["tabs"] = tabs, ["activeTabId"] = session.ActiveTabId, ["userControlled"] = session.UserControlled }));
        }
    }

    // Download progress changes no published field, so its events coalesce into one trailing publish per burst; the staleness bound keeps UpdatedAt live during long downloads.
    private static readonly TimeSpan DownloadsCoalesceInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan DownloadsStalenessBound = TimeSpan.FromSeconds(1);

    private void PublishDownloadsCoalesced(SessionId id, SessionEntry session)
    {
        lock (_sync)
        {
            if (_disposed) return;
            var due = DateTimeOffset.UtcNow - session.Status.UpdatedAt >= DownloadsStalenessBound
                ? TimeSpan.Zero
                : DownloadsCoalesceInterval;
            if (session.DownloadsTimer is null)
                session.DownloadsTimer = new Timer(_ => Publish(id, session), null, due, Timeout.InfiniteTimeSpan);
            else
                session.DownloadsTimer.Change(due, Timeout.InfiniteTimeSpan);
        }
    }

    private void Publish(SessionId id, SessionEntry session)
    {
        BrowserSessionStatus status;
        lock (_sync)
        {
            if (_disposed) return;
            var tabs = new BrowserTabStatus[session.Tabs.Count];
            var index = 0;
            BrowserTabStatus? active = null;
            foreach (var tab in session.Tabs.Values)
            {
                var item = new BrowserTabStatus(
                    tab.Page.Id, tab.Automation.Url ?? "about:blank", tab.Automation.Title ?? "New tab",
                    tab.Page.Id == session.ActiveTabId, IsLoading: tab.Loading, IsUserControlled: session.UserControlled);
                tabs[index++] = item;
                if (active is null && item.IsActive) active = item;
            }
            status = new BrowserSessionStatus(
                session.Busy ? BrowserSessionState.Busy : session.Error is not null ? BrowserSessionState.Failed
                    : tabs.Length > 0 ? BrowserSessionState.Launched : BrowserSessionState.Idle,
                active?.Url, active?.Title, UpdatedAt: DateTimeOffset.UtcNow, Tabs: tabs,
                ActiveTabId: session.ActiveTabId, IsUserControlled: session.UserControlled, Error: session.Error);
            session.Status = status;
            // An immediate publish already reflects everything a pending progress debounce would publish.
            session.DownloadsTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        StatusChanged?.Invoke(this, new(id, status));
    }

    private SessionEntry GetSession(SessionId id)
    {
        if (!_sessions.TryGetValue(id, out var entry))
        {
            entry = new();
            _sessions.Add(id, entry);
        }
        return entry;
    }

    private TabEntry RequireTab(SessionEntry session, string? id)
    {
        lock (_sync)
        {
            if (id is null || !session.Tabs.TryGetValue(id, out var tab))
                throw new InvalidOperationException("The tab does not belong to this session or has been closed. List tabs before retrying.");
            return tab;
        }
    }

    private static bool IsObservation(BrowserAction action) => action.Verb is
        "tabs" or "capabilities" or "snapshot" or "dom_snapshot" or "screenshot" or "read_text" or "count"
        or "inner_text" or "text_content" or "get_attribute" or "is_visible" or "is_enabled" or "element_info"
        or "downloads" or "record_status" or "record_cancel" or "takeover";

    private static string? GetString(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string PlatformName() => OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "other";

    private static void ValidateUrl(string url)
    {
        if (url == "about:blank") return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Browser navigation accepts only http, https, or about:blank URLs.");
    }

    private static BrowserActionResult Failure(string error, bool uncertain = false) =>
        new(false, Error: error, SideEffectUncertain: uncertain);

    private static void Detach(TabEntry entry)
    {
        entry.Automation.StateChanged -= entry.StateChanged;
        entry.Automation.DownloadsChanged -= entry.DownloadsChanged;
        entry.Page.ProtocolEvent -= entry.ProtocolEvent;
        entry.Automation.Dispose();
    }

    public void Dispose()
    {
        TabEntry[] tabs;
        Timer?[] timers;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            foreach (var session in _sessions.Values) session.Inflight?.Cancel();
            tabs = _sessions.Values.SelectMany(session => session.Tabs.Values).ToArray();
            timers = _sessions.Values.Select(session => session.DownloadsTimer).Where(timer => timer is not null).ToArray();
            _sessions.Clear();
            _reservedTabs = 0;
        }
        foreach (var timer in timers) timer!.Dispose();
        foreach (var tab in tabs) Detach(tab);
        _host.Dispose();
    }

    private sealed record PopupAction(string OpenerTabId, string Url, int ControlGeneration, string? AdoptTargetId = null) : BrowserAction
    {
        public override string Verb => "popup";
    }

    private sealed class SessionEntry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<string, TabEntry> Tabs { get; } = [];
        public string? ActiveTabId { get; set; }
        public bool UserControlled { get; set; }
        public int ControlGeneration { get; set; }
        public bool Busy { get; set; }
        public string? Error { get; set; }
        public CancellationTokenSource? Inflight { get; set; }
        public Timer? DownloadsTimer { get; set; }
        public BrowserSessionStatus Status { get; set; } = new(BrowserSessionState.Idle);
    }

    private sealed class TabEntry(IBrowserPage page, CdpBrowserAutomation automation)
    {
        public IBrowserPage Page { get; } = page;
        public CdpBrowserAutomation Automation { get; } = automation;
        public bool Keep { get; set; }
        public bool Loading { get; set; }
        public EventHandler? StateChanged { get; set; }
        public EventHandler? DownloadsChanged { get; set; }
        public EventHandler<BrowserProtocolEvent>? ProtocolEvent { get; set; }
    }
}
