using Alcoler.Contracts.Browser;
using Alcoler.Contracts.Identifiers;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Assert = Xunit.Assert;

namespace Alcoler.Workbench.Tests.Browser;

public class BrowserPaneViewModelTests
{
    private static SessionId Other => new("sess_other");

    [AvaloniaFact]
    public void Initial_State_IsIdle_WithoutScreenshot()
    {
        using var viewModel = Create(out _);

        Assert.Equal("Idle", viewModel.StateText);
        Assert.Null(viewModel.Url);
        Assert.False(viewModel.HasScreenshot);
        Assert.False(viewModel.CanInteract);
    }

    [AvaloniaFact]
    public void Bind_ActivatesSession_AndPullsControllerStatus()
    {
        using var viewModel = Create(out var controller);
        var sessionId = new SessionId("sess_active");
        controller.Status = new BrowserSessionStatus(
            BrowserSessionState.Busy, "https://example.com", "Example", UpdatedAt: DateTimeOffset.UtcNow);

        viewModel.Bind(sessionId);

        Assert.True(viewModel.CanInteract);
        Assert.Equal("Busy", viewModel.StateText);
        Assert.Equal("https://example.com", viewModel.Url);
        Assert.Equal("Example", viewModel.Title);
        Assert.NotEmpty(viewModel.UpdatedText);
    }

    [AvaloniaFact]
    public void Bind_ResetsToIdle_WhenSessionIsNull()
    {
        using var viewModel = Create(out _);
        viewModel.Bind(new SessionId("sess_active"));
        viewModel.Bind(null);

        Assert.Null(viewModel.ActiveSessionId);
        Assert.False(viewModel.CanInteract);
        Assert.Equal("Idle", viewModel.StateText);
    }

    [AvaloniaFact]
    public void StatusEvent_ForActiveSession_UpdatesViewModel()
    {
        using var viewModel = Create(out var controller);
        var sessionId = new SessionId("sess_active");
        viewModel.Bind(sessionId);

        controller.RaiseStatusChanged(
            sessionId,
            new BrowserSessionStatus(BrowserSessionState.Launched, "https://example.com/page", "Page"));

        Assert.Equal("Launched", viewModel.StateText);
        Assert.Equal("https://example.com/page", viewModel.Url);
    }

    [AvaloniaFact]
    public void StatusEvent_ForOtherSession_IsIgnored()
    {
        using var viewModel = Create(out var controller);
        viewModel.Bind(new SessionId("sess_active"));

        controller.RaiseStatusChanged(
            Other, new BrowserSessionStatus(BrowserSessionState.Failed, "https://elsewhere.invalid"));

        Assert.Equal("Idle", viewModel.StateText);
        Assert.Null(viewModel.Url);
    }

    [AvaloniaFact]
    public void StatusEvent_AfterDispose_IsIgnored()
    {
        var viewModel = Create(out var controller);
        var sessionId = new SessionId("sess_active");
        viewModel.Bind(sessionId);
        viewModel.Dispose();

        controller.RaiseStatusChanged(
            sessionId, new BrowserSessionStatus(BrowserSessionState.Failed, "https://example.com"));

        Assert.Equal("Idle", viewModel.StateText);
    }

    [AvaloniaFact]
    public void Screenshot_LoadsThroughInjector_WhenUriChanges()
    {
        var loaded = new List<string>();
        using var viewModel = Create(
            out var controller, uri => { loaded.Add(uri); return new FakeImage(); });
        var sessionId = new SessionId("sess_active");
        viewModel.Bind(sessionId);

        var first = @"C:\shots\sess_active.png";
        controller.RaiseStatusChanged(
            sessionId, new BrowserSessionStatus(BrowserSessionState.Launched, "https://example.com", LatestScreenshotUri: first));

        Assert.True(viewModel.HasScreenshot);
        Assert.IsType<FakeImage>(viewModel.Screenshot);
        Assert.Equal([first], loaded);

        controller.RaiseStatusChanged(
            sessionId, new BrowserSessionStatus(BrowserSessionState.Launched, "https://example.com", LatestScreenshotUri: first));
        Assert.Single(loaded);
    }

    [AvaloniaFact]
    public void Screenshot_IsSkipped_WhenNoInjector()
    {
        using var viewModel = Create(out var controller);
        var sessionId = new SessionId("sess_active");
        viewModel.Bind(sessionId);

        controller.RaiseStatusChanged(
            sessionId,
            new BrowserSessionStatus(BrowserSessionState.Launched, "https://example.com", LatestScreenshotUri: @"C:\shots\s.png"));

        Assert.False(viewModel.HasScreenshot);
        Assert.Null(viewModel.Screenshot);
    }

    [AvaloniaFact]
    public void Screenshot_LoadFailure_IsSurvived()
    {
        using var viewModel = Create(out var controller, _ => throw new IOException("locked"));
        var sessionId = new SessionId("sess_active");
        viewModel.Bind(sessionId);

        controller.RaiseStatusChanged(
            sessionId,
            new BrowserSessionStatus(BrowserSessionState.Launched, "https://example.com", LatestScreenshotUri: @"C:\shots\s.png"));

        Assert.False(viewModel.HasScreenshot);
    }

    [AvaloniaFact]
    public void RefreshCommand_ReReadsControllerStatus()
    {
        using var viewModel = Create(out var controller);
        var sessionId = new SessionId("sess_active");
        viewModel.Bind(sessionId);
        Assert.Equal("Idle", viewModel.StateText);

        controller.Status = new BrowserSessionStatus(BrowserSessionState.Busy, "https://example.com/busy");
        viewModel.RefreshCommand.Execute(null);

        Assert.Equal("Busy", viewModel.StateText);
        Assert.Equal("https://example.com/busy", viewModel.Url);
    }

    [AvaloniaFact]
    public void TakeOverCommand_BringsActiveSessionToFront()
    {
        using var viewModel = Create(out var controller);
        var sessionId = new SessionId("sess_active");
        viewModel.Bind(sessionId);

        viewModel.TakeOverCommand.Execute(null);

        Assert.Equal([sessionId], controller.Takeovers);
    }

    private static BrowserPaneViewModel Create(
        out FakeBrowserController controller, Func<string, IImage?>? imageLoader = null)
    {
        controller = new FakeBrowserController();
        return new BrowserPaneViewModel(controller, InlineUiScheduler.Instance, imageLoader);
    }

    private sealed class FakeImage : IImage
    {
        public Size Size => new(4, 4);

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }
    }

    private sealed class FakeBrowserController : IBrowserController
    {
        public List<SessionId> Takeovers { get; } = [];

        public BrowserSessionStatus? Status { get; set; }

        public event EventHandler<BrowserStatusChangedEventArgs>? StatusChanged;

        public void RaiseStatusChanged(SessionId sessionId, BrowserSessionStatus status) =>
            StatusChanged?.Invoke(this, new BrowserStatusChangedEventArgs(sessionId, status));

        public Task<BrowserActionResult> ExecuteAsync(SessionId sessionId, BrowserAction action, CancellationToken cancellationToken) =>
            Task.FromResult(new BrowserActionResult(Ok: true));

        public Task CancelAsync(SessionId sessionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task TakeoverAsync(SessionId sessionId, CancellationToken cancellationToken)
        {
            Takeovers.Add(sessionId);
            return Task.CompletedTask;
        }

        public BrowserSessionStatus? GetStatus(SessionId sessionId) => Status;

        public void Dispose()
        {
        }
    }
}
