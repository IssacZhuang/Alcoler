using System.Text.Json;
using Alcoler.Contracts.Browser;
using Alcoler.Contracts.Identifiers;
using Xunit;

namespace Alcoler.Browser.Tests;

public sealed class BrowserControllerTests
{
    private static readonly SessionId First = new("session_first");
    private static readonly SessionId Second = new("session_second");

    [Fact]
    public async Task Tabs_AreOwnedByTheirSession()
    {
        using var host = new TestHost();
        using var controller = new BrowserController(host);
        var first = await controller.ExecuteAsync(First, Command("new_tab"), default);
        var second = await controller.ExecuteAsync(Second, Command("new_tab"), default);
        Assert.True(first.Ok, first.Error);
        Assert.True(second.Ok, second.Error);
        Assert.NotEqual(first.TabId, second.TabId);
        var forbidden = await controller.ExecuteAsync(Second, Command("select_tab", first.TabId), default);
        Assert.False(forbidden.Ok);
        Assert.Contains("does not belong", forbidden.Error);
        Assert.Equal(second.TabId, controller.GetStatus(Second)!.ActiveTabId);
        Assert.Equal(2, host.Pages.Count);
    }

    [Fact]
    public async Task ClosingSelectedTab_SelectsRemainingTab_AndClosesNativePage()
    {
        using var host = new TestHost();
        using var controller = new BrowserController(host);
        var first = await controller.ExecuteAsync(First, Command("new_tab"), default);
        var second = await controller.ExecuteAsync(First, Command("new_tab"), default);
        var closed = await controller.ExecuteAsync(First, Command("close_tab", second.TabId), default);
        Assert.True(closed.Ok, closed.Error);
        Assert.Equal(first.TabId, controller.GetStatus(First)!.ActiveTabId);
        Assert.Equal(second.TabId, Assert.Single(host.Closed));
        Assert.Single(controller.GetStatus(First)!.Tabs!);
    }

    [Fact]
    public async Task OpenPopupAsync_ForwardsTheHostTargetIdForAdoption()
    {
        using var host = new TestHost();
        using var controller = new BrowserController(host);
        var opener = await controller.ExecuteAsync(First, Command("new_tab"), default);
        Assert.True(opener.Ok, opener.Error);
        var popup = await controller.OpenPopupAsync(First, opener.TabId!, "about:blank", default, adoptTargetId: "POP9");
        Assert.True(popup.Ok, popup.Error);
        Assert.Equal((popup.TabId!, "POP9"), Assert.Single(host.Adoptions));
        Assert.Equal(2, host.Pages.Count);
        var plain = await controller.ExecuteAsync(First, Command("new_tab"), default);
        Assert.True(plain.Ok, plain.Error);
        Assert.Single(host.Adoptions);
    }

    [Fact]
    public async Task Takeover_BlocksAutomatedInputAndResume_ButAllowsObservationAndUserResume()
    {
        using var host = new TestHost();
        using var controller = new BrowserController(host);
        var tab = await controller.ExecuteAsync(First, Command("new_tab"), default);
        await controller.TakeoverAsync(First, default);
        Assert.True(controller.GetStatus(First)!.IsUserControlled);
        Assert.Equal(tab.TabId, Assert.Single(host.Shown));
        var blocked = await controller.ExecuteAsync(First, new BrowserNavigateAction("https://example.com"), default);
        Assert.False(blocked.Ok);
        Assert.Contains("human control", blocked.Error);
        var forbiddenResume = await controller.ExecuteAsync(First, Command("resume"), default);
        Assert.False(forbiddenResume.Ok);
        Assert.True((await controller.ExecuteAsync(First, Command("tabs"), default)).Ok);
        var recording = await controller.ExecuteAsync(First, Command("record_status"), default);
        Assert.True(recording.Ok, recording.Error);
        Assert.Null(recording.Media);
        using var state = JsonDocument.Parse(recording.StructuredOutput!);
        Assert.Equal("cancelled", state.RootElement.GetProperty("state").GetString());
        var resumed = await controller.ExecuteAsync(First, Command("resume") with { UserInitiated = true }, default);
        Assert.True(resumed.Ok, resumed.Error);
        Assert.False(controller.GetStatus(First)!.IsUserControlled);
    }

    [Fact]
    public async Task Takeover_StillGatesControlWhenBackendHasNoWindow()
    {
        var host = new TestHost { ShowThrows = true };
        using var controller = new BrowserController(host);
        var tab = await controller.ExecuteAsync(First, Command("new_tab"), default);
        Assert.True(tab.Ok, tab.Error);
        await controller.TakeoverAsync(First, default);
        Assert.True(controller.GetStatus(First)!.IsUserControlled);
        Assert.Empty(host.Shown);
        var blocked = await controller.ExecuteAsync(First, new BrowserNavigateAction("https://example.com"), default);
        Assert.False(blocked.Ok);
        Assert.Contains("human control", blocked.Error);
        var resumed = await controller.ExecuteAsync(First, Command("resume") with { UserInitiated = true }, default);
        Assert.True(resumed.Ok, resumed.Error);
    }

    [Fact]
    public async Task TakeoverAction_DoesNotCancelItsOwnResult()
    {
        using var host = new TestHost();
        using var controller = new BrowserController(host);
        await controller.ExecuteAsync(First, Command("new_tab"), default);
        var result = await controller.ExecuteAsync(First, Command("takeover"), default);
        Assert.True(result.Ok, result.Error);
        Assert.True(controller.GetStatus(First)!.IsUserControlled);
    }

    [Fact]
    public async Task Takeover_WaitsForCanceledCommandCleanupBeforeShowingPage()
    {
        using var host = new TestHost();
        using var controller = new BrowserController(host);
        var tab = await controller.ExecuteAsync(First, Command("new_tab"), default);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Pages[tab.TabId!].Intercept = async (method, ct) =>
        {
            if (method != "Page.reload") return null;
            using var registration = ct.Register(() => canceled.TrySetResult());
            started.TrySetResult();
            await drained.Task;
            ct.ThrowIfCancellationRequested();
            return BrowserJson.Element(null);
        };
        var operation = controller.ExecuteAsync(First, Command("reload", tab.TabId), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedPopup = controller.OpenPopupAsync(First, tab.TabId!, "about:blank", default, isUserInitiated: true);
        var takeover = controller.TakeoverAsync(First, default);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(takeover.IsCompleted);
        Assert.Empty(host.Shown);
        drained.TrySetResult();
        var result = await operation.WaitAsync(TimeSpan.FromSeconds(5));
        await takeover.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Ok);
        Assert.True(result.SideEffectUncertain);
        Assert.False((await queuedPopup.WaitAsync(TimeSpan.FromSeconds(5))).Ok);
        Assert.Single(host.Pages);
        Assert.Equal(tab.TabId, Assert.Single(host.Shown));
    }

    [Fact]
    public async Task Cap_IsAtomicAcrossSessions_AndClosingReleasesSlot()
    {
        using var host = new TestHost();
        using var controller = new BrowserController(host);
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(index => controller.ExecuteAsync(
            new SessionId($"session_{index}"), Command("new_tab"), default)));
        Assert.Equal(32, results.Count(result => result.Ok));
        Assert.Equal(32, host.Pages.Count);
        var existing = results.First(result => result.Ok);
        var owner = host.Owners[existing.TabId!];
        Assert.True((await controller.ExecuteAsync(owner, Command("close_tab", existing.TabId), default)).Ok);
        Assert.True((await controller.ExecuteAsync(First, Command("new_tab"), default)).Ok);
    }

    [Fact]
    public async Task Finalize_KeepsUnlistedTabs_AndCanReleaseOwnership()
    {
        using var host = new TestHost();
        using var controller = new BrowserController(host);
        var first = await controller.ExecuteAsync(First, Command("new_tab"), default);
        await controller.ExecuteAsync(First, Command("new_tab"), default);
        var input = BrowserJson.Element(new Dictionary<string, object?> { ["keep"] = new[] { first.TabId }, ["release"] = true });
        var result = await controller.ExecuteAsync(First, new BrowserCommandAction("finalize", input), default);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, controller.GetStatus(First)!.Tabs!.Count);
        Assert.True(controller.GetStatus(First)!.IsUserControlled);
        Assert.Empty(host.Closed);
    }

    [Fact]
    public async Task FailedCreation_ReleasesReservedSlotAndDoesNotLeaveTab()
    {
        using var host = new TestHost { FailCreation = true };
        using var controller = new BrowserController(host);
        var failure = await controller.ExecuteAsync(First, Command("new_tab"), default);
        Assert.False(failure.Ok);
        Assert.Empty(controller.GetStatus(First)!.Tabs!);
        Assert.Equal(BrowserSessionState.Failed, controller.GetStatus(First)!.State);
        host.FailCreation = false;
        var success = await controller.ExecuteAsync(First, Command("new_tab"), default);
        Assert.True(success.Ok, success.Error);
        Assert.Equal(BrowserSessionState.Launched, controller.GetStatus(First)!.State);
    }

    [Fact]
    public async Task NativeClose_UpdatesStatus_AndRejectsStaleTab()
    {
        using var host = new TestHost();
        using var controller = new BrowserController(host);
        var tab = await controller.ExecuteAsync(First, Command("new_tab"), default);
        host.Pages[tab.TabId!].Raise("Host.closed");
        Assert.Empty(controller.GetStatus(First)!.Tabs!);
        Assert.Null(controller.GetStatus(First)!.ActiveTabId);
        Assert.False((await controller.ExecuteAsync(First, Command("select_tab", tab.TabId), default)).Ok);
        await host.CloseCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(host.Pages);
    }

    [Fact]
    public async Task Popup_RequiresLiveOpener_AndRejectsExternalProtocols()
    {
        using var host = new TestHost();
        using var controller = new BrowserController(host);
        var tab = await controller.ExecuteAsync(First, Command("new_tab"), default);
        Assert.False((await controller.OpenPopupAsync(Second, tab.TabId!, "https://example.com", default)).Ok);
        var invalid = await controller.OpenPopupAsync(First, tab.TabId!, "file:///C:/secret", default);
        Assert.False(invalid.Ok);
        Assert.Single(host.Pages);
    }

    [Fact]
    public async Task Observation_UnderHumanControlDoesNotCreateTab()
    {
        using var host = new TestHost();
        using var controller = new BrowserController(host);
        await controller.TakeoverAsync(First, default);
        var screenshot = await controller.ExecuteAsync(First, new BrowserScreenshotAction(), default);
        Assert.False(screenshot.Ok);
        Assert.Empty(host.Pages);
    }

    [Fact]
    public async Task Popup_DoesNotElevateScriptToHumanInteraction()
    {
        using var host = new TestHost();
        using var controller = new BrowserController(host);
        var tab = await controller.ExecuteAsync(First, Command("new_tab"), default);
        await controller.TakeoverAsync(First, default);
        var scriptPopup = await controller.OpenPopupAsync(First, tab.TabId!, "https://example.com", default);
        Assert.False(scriptPopup.Ok);
        Assert.Single(host.Pages);
        var humanPopup = await controller.OpenPopupAsync(First, tab.TabId!, "about:blank", default, isUserInitiated: true);
        Assert.True(humanPopup.Ok, humanPopup.Error);
        Assert.Equal(2, host.Pages.Count);
    }

    [Fact]
    public async Task VisibilityFalse_ParksNativePage()
    {
        using var host = new TestHost();
        using var controller = new BrowserController(host);
        var tab = await controller.ExecuteAsync(First, Command("new_tab"), default);
        var result = await controller.ExecuteAsync(First, new BrowserCommandAction("visibility",
            BrowserJson.Element(new Dictionary<string, object?> { ["visible"] = false })) { TabId = tab.TabId }, default);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(tab.TabId, Assert.Single(host.Hidden));
    }

    [Fact]
    public void JsonValues_AreWrittenWithoutReflection()
    {
        var value = BrowserJson.Element(new Dictionary<string, object?>
        {
            ["text"] = "\"</script>\n", ["items"] = new object?[] { 1, true, null, new Dictionary<string, object?> { ["x"] = 1.5 } },
        });
        Assert.Equal("\"</script>\n", value.GetProperty("text").GetString());
        Assert.Equal(1.5, value.GetProperty("items")[3].GetProperty("x").GetDouble());
        Assert.Throws<ArgumentException>(() => BrowserJson.Serialize(new object()));
    }

    private static BrowserCommandAction Command(string action, string? tab = null) =>
        new(action, BrowserJson.Element(new Dictionary<string, object?>())) { TabId = tab };

    private sealed class TestHost : IBrowserHost
    {
        private readonly Lock _sync = new();
        public Dictionary<string, TestPage> Pages { get; } = [];
        public Dictionary<string, SessionId> Owners { get; } = [];
        public List<string> Closed { get; } = [];
        public List<string> Shown { get; } = [];
        public List<string> Hidden { get; } = [];
        public List<(string TabId, string AdoptTargetId)> Adoptions { get; } = [];
        public bool FailCreation { get; set; }
        public bool ShowThrows { get; set; }
        public TaskCompletionSource CloseCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<BrowserNewTabRequestedEventArgs>? NewTabRequested
        {
            add { }
            remove { }
        }
        public Task<IBrowserPage> CreatePageAsync(SessionId sessionId, string tabId, CancellationToken cancellationToken, string? adoptTargetId = null)
        {
            if (FailCreation) throw new InvalidOperationException("Native creation failed.");
            lock (_sync)
            {
                if (adoptTargetId is not null) Adoptions.Add((tabId, adoptTargetId));
                var page = new TestPage(tabId);
                Pages.Add(tabId, page);
                Owners.Add(tabId, sessionId);
                return Task.FromResult<IBrowserPage>(page);
            }
        }
        public Task ClosePageAsync(string tabId, CancellationToken cancellationToken)
        {
            lock (_sync) { Pages.Remove(tabId); Closed.Add(tabId); }
            CloseCompleted.TrySetResult();
            return Task.CompletedTask;
        }
        public Task ShowPageAsync(string tabId, CancellationToken cancellationToken)
        {
            if (ShowThrows) throw new InvalidOperationException("Showing a browser page is not available in headless mode.");
            Shown.Add(tabId);
            return Task.CompletedTask;
        }
        public Task HidePageAsync(string tabId, CancellationToken cancellationToken)
        {
            Hidden.Add(tabId);
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }

    private sealed class TestPage(string id) : IBrowserPage
    {
        public string Id { get; } = id;
        public event EventHandler<BrowserProtocolEvent>? ProtocolEvent;
        public Func<string, CancellationToken, Task<JsonElement?>>? Intercept { get; set; }
        public void Raise(string method) => ProtocolEvent?.Invoke(this, new(method, BrowserJson.Element(new Dictionary<string, object?>())));
        public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken cancellationToken, string? sessionId = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Intercept is not null && await Intercept(method, cancellationToken) is { } intercepted) return intercepted;
            return method switch
            {
                "Page.getFrameTree" => JsonDocument.Parse("""{"frameTree":{"frame":{"id":"main","url":"about:blank"}}}""").RootElement.Clone(),
                _ => BrowserJson.Element(new Dictionary<string, object?>()),
            };
        }
        public void Dispose() { }
    }
}
