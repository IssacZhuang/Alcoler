using Alcoler.Contracts.Browser;
using Alcoler.Contracts.Identifiers;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Alcoler.Workbench.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    [AvaloniaFact]
    public void Screenshot_ReloadsSamePathWhenVersionChanges_AndDisposesPreviousImage()
    {
        var images = new List<FakeImage>();
        using var viewModel = Create(out var controller, _ =>
        {
            var image = new FakeImage();
            images.Add(image);
            return image;
        });
        var sessionId = new SessionId("sess_active");
        viewModel.Bind(sessionId);
        var first = new BrowserSessionStatus(BrowserSessionState.Launched,
            LatestScreenshotUri: @"C:\shots\sess_active.png", UpdatedAt: DateTimeOffset.UtcNow);

        controller.RaiseStatusChanged(sessionId, first);
        controller.RaiseStatusChanged(sessionId, first);
        Assert.Single(images);
        controller.RaiseStatusChanged(sessionId, first with { UpdatedAt = first.UpdatedAt.AddSeconds(1) });

        Assert.Equal(2, images.Count);
        Assert.True(images[0].IsDisposed);
        Assert.Same(images[1], viewModel.Screenshot);
        Assert.False(images[1].IsDisposed);

        viewModel.Bind(Other);
        Assert.False(viewModel.HasScreenshot);
        Assert.True(images[1].IsDisposed);
    }

    [AvaloniaFact]
    public void Dispose_ReleasesCurrentScreenshot_OnlyOnce()
    {
        var image = new FakeImage();
        var viewModel = Create(out var controller, _ => image);
        var sessionId = new SessionId("sess_active");
        viewModel.Bind(sessionId);
        controller.RaiseStatusChanged(sessionId, new BrowserSessionStatus(
            BrowserSessionState.Launched, LatestScreenshotUri: @"C:\shots\active.png"));

        viewModel.Dispose();
        viewModel.Dispose();

        Assert.True(image.IsDisposed);
        Assert.False(viewModel.HasScreenshot);
        Assert.Equal(1, image.DisposeCount);
    }

    [AvaloniaFact]
    public async Task NavigateCommand_UsesExistingControllerAction_AndReportsFailures()
    {
        using var viewModel = Create(out var controller);
        viewModel.Address = "example.com";
        Assert.False(viewModel.NavigateCommand.CanExecute(null));
        Assert.False(viewModel.TakeOverCommand.CanExecute(null));
        var sessionId = new SessionId("sess_active");
        viewModel.Bind(sessionId);
        viewModel.Address = "example.com";

        await viewModel.NavigateCommand.ExecuteAsync(null);

        var request = Assert.Single(controller.Actions);
        Assert.Equal(sessionId, request.SessionId);
        Assert.Equal("https://example.com", Assert.IsType<BrowserNavigateAction>(request.Action).Url);
        Assert.False(viewModel.IsNavigating);
        Assert.Null(viewModel.Banner);

        controller.Result = new BrowserActionResult(false, Error: "unreachable");
        viewModel.Address = "http://localhost:3000";
        await viewModel.NavigateCommand.ExecuteAsync(null);
        Assert.Equal("unreachable", viewModel.Banner);
        Assert.Equal("http://localhost:3000", Assert.IsType<BrowserNavigateAction>(controller.Actions[^1].Action).Url);
    }

    [AvaloniaFact]
    public void BrowserPaneView_AddressEnterNavigates_AndScreenshotUsesAvailableViewport()
    {
        var image = new FakeImage();
        using var viewModel = Create(out var controller, _ => image);
        var sessionId = new SessionId("sess_active");
        viewModel.Bind(sessionId);
        controller.RaiseStatusChanged(sessionId, new BrowserSessionStatus(
            BrowserSessionState.Launched, "https://example.com", "Example", @"C:\shots\active.png"));
        var view = new BrowserPaneView { DataContext = viewModel };
        var window = new Window { Width = 360, Height = 480, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var address = view.FindControl<TextBox>("PART_BrowserAddress")!;
            var toolbar = view.FindControl<Grid>("PART_BrowserToolbar")!;
            var screenshot = view.FindControl<Image>("PART_BrowserScreenshot")!;
            Assert.Equal(48, toolbar.Bounds.Height);
            Assert.Equal("https://example.com", address.Text);
            Assert.Equal(Stretch.Uniform, screenshot.Stretch);
            Assert.Same(image, screenshot.Source);
            Assert.True(screenshot.IsVisible);
            address.Text = "https://example.com/next";
            address.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal("https://example.com/next",
                Assert.IsType<BrowserNavigateAction>(Assert.Single(controller.Actions).Action).Url);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(),
                button => button.Content is string text && text is "Back" or "Forward");
        }
        finally
        {
            window.Close();
        }
    }

    private static BrowserPaneViewModel Create(
        out FakeBrowserController controller, Func<string, IImage?>? imageLoader = null)
    {
        controller = new FakeBrowserController();
        return new BrowserPaneViewModel(controller, InlineUiScheduler.Instance, imageLoader);
    }

    private sealed class FakeImage : IImage, IDisposable
    {
        public Size Size => new(4, 4);

        public bool IsDisposed => DisposeCount > 0;

        public int DisposeCount { get; private set; }

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeBrowserController : IBrowserController
    {
        public List<SessionId> Takeovers { get; } = [];

        public List<(SessionId SessionId, BrowserAction Action)> Actions { get; } = [];

        public BrowserActionResult Result { get; set; } = new(Ok: true);

        public BrowserSessionStatus? Status { get; set; }

        public event EventHandler<BrowserStatusChangedEventArgs>? StatusChanged;

        public void RaiseStatusChanged(SessionId sessionId, BrowserSessionStatus status) =>
            StatusChanged?.Invoke(this, new BrowserStatusChangedEventArgs(sessionId, status));

        public Task<BrowserActionResult> ExecuteAsync(SessionId sessionId, BrowserAction action, CancellationToken cancellationToken)
        {
            Actions.Add((sessionId, action));
            return Task.FromResult(Result);
        }

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
