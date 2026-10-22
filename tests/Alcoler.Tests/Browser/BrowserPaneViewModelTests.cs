using Alcoler.Contracts.Browser;
using Alcoler.Contracts.Identifiers;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Alcoler.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Assert = Xunit.Assert;

namespace Alcoler.Tests.Browser;

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
    public void TakeOverCommand_PausesActiveSessionThroughController()
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
        Assert.True(request.Action.UserInitiated);
        Assert.False(viewModel.IsNavigating);
        Assert.Null(viewModel.Banner);

        controller.Result = new BrowserActionResult(false, Error: "unreachable");
        viewModel.Address = "http://localhost:3000";
        await viewModel.NavigateCommand.ExecuteAsync(null);
        Assert.Equal("unreachable", viewModel.Banner);
        Assert.Equal("http://localhost:3000", Assert.IsType<BrowserNavigateAction>(controller.Actions[^1].Action).Url);
    }

    [AvaloniaFact]
    public void BrowserPaneView_AddressEnterNavigates_AndScreenshotRemainsOptionalFallback()
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
            Assert.NotNull(view.FindControl<Button>("PART_BrowserBack"));
            Assert.NotNull(view.FindControl<Button>("PART_BrowserForward"));
            Assert.Same(viewModel.ReloadCommand, view.FindControl<Button>("PART_BrowserRefresh")!.Command);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Status_ProjectsTabsSelectionOwnershipAndErrors()
    {
        using var viewModel = Create(out var controller);
        var sessionId = new SessionId("sess_active");
        viewModel.Bind(sessionId);
        var status = TabStatus("second") with { Error = "WebView is unsupported on this platform.", IsUserControlled = true };

        controller.RaiseStatusChanged(sessionId, status);

        Assert.Equal(2, viewModel.Tabs.Count);
        Assert.False(viewModel.Tabs[0].IsActive);
        Assert.True(viewModel.Tabs[1].IsActive);
        Assert.Same(viewModel.Tabs[1], viewModel.SelectedTab);
        Assert.Equal("second", viewModel.ActiveTabId);
        Assert.Equal("https://example.com/second", viewModel.Address);
        Assert.Equal("Second", viewModel.Title);
        Assert.True(viewModel.IsUserControlled);
        Assert.Contains("paused", viewModel.OwnershipText);
        Assert.Equal(status.Error, viewModel.Banner);
        Assert.False(viewModel.TakeOverCommand.CanExecute(null));
        Assert.True(viewModel.ResumeCommand.CanExecute(null));

        controller.RaiseStatusChanged(sessionId, TabStatus("first") with { Tabs = [] , ActiveTabId = null });

        Assert.Empty(viewModel.Tabs);
        Assert.Null(viewModel.SelectedTab);
        Assert.Null(viewModel.ActiveTabId);
        Assert.False(viewModel.IsUserControlled);
        Assert.Null(viewModel.Banner);
    }

    [AvaloniaFact]
    public void Presenter_IsLazy_AndBoundViewsInheritFactoryWithoutRootCompeting()
    {
        var presenters = new List<FakePresenter>();
        using var root = Create(out var controller, pagePresenterFactory: id =>
        {
            var presenter = new FakePresenter(id);
            presenters.Add(presenter);
            return presenter;
        });
        var sessionId = new SessionId("sess_active");
        controller.Status = TabStatus("first");
        root.Bind(sessionId);
        using var bound = root.CreateBoundView(sessionId);

        Assert.Empty(presenters);
        Assert.Null(root.PagePresenter);
        Assert.Null(bound.PagePresenter);

        bound.AttachView();

        Assert.Single(presenters);
        Assert.Same(presenters[0], bound.PagePresenter);
        Assert.Null(root.PagePresenter);
        Assert.True(bound.HasPagePresenter);
        Assert.False(bound.ShowEmptyState);
        bound.AttachView();
        Assert.Single(presenters);
    }

    [AvaloniaFact]
    public void Presenter_OnlyChangesWhenActiveIdChanges_AndReleasesOldWrapper()
    {
        var presenters = new List<FakePresenter>();
        using var viewModel = Create(out var controller, pagePresenterFactory: id =>
        {
            var presenter = new FakePresenter(id);
            presenters.Add(presenter);
            return presenter;
        });
        var sessionId = new SessionId("sess_active");
        controller.Status = TabStatus("first");
        viewModel.Bind(sessionId);
        viewModel.AttachView();
        var first = viewModel.PagePresenter;

        controller.RaiseStatusChanged(sessionId, TabStatus("first") with { UpdatedAt = DateTimeOffset.UtcNow });
        Assert.Same(first, viewModel.PagePresenter);
        Assert.Single(presenters);

        controller.RaiseStatusChanged(sessionId, TabStatus("second"));
        Assert.Equal(2, presenters.Count);
        Assert.Equal("second", presenters[1].TabId);
        Assert.Same(presenters[1], viewModel.PagePresenter);
        Assert.Equal(1, presenters[0].DisposeCount);

        controller.RaiseStatusChanged(sessionId, new BrowserSessionStatus(BrowserSessionState.Idle, Tabs: []));
        Assert.Null(viewModel.PagePresenter);
        Assert.Equal(1, presenters[1].DisposeCount);
    }

    [AvaloniaFact]
    public void BindDetachAndDispose_ReleasePresentersWithoutDestroyingBrowserLifetime()
    {
        var presenters = new List<FakePresenter>();
        var viewModel = Create(out var controller, pagePresenterFactory: id =>
        {
            var presenter = new FakePresenter(id);
            presenters.Add(presenter);
            return presenter;
        });
        controller.Status = TabStatus("first");
        viewModel.Bind(new SessionId("sess_active"));
        viewModel.AttachView();
        viewModel.Bind(Other);
        Assert.Equal(2, presenters.Count);
        Assert.Equal(1, presenters[0].DisposeCount);
        viewModel.DetachView();
        viewModel.DetachView();
        Assert.Equal(1, presenters[1].DisposeCount);
        Assert.Null(viewModel.PagePresenter);
        viewModel.AttachView();
        Assert.Equal(3, presenters.Count);

        viewModel.Dispose();
        viewModel.Dispose();
        controller.RaiseStatusChanged(Other, TabStatus("second"));

        Assert.Equal(1, presenters[2].DisposeCount);
        Assert.Null(viewModel.PagePresenter);
        Assert.Equal(0, controller.DisposeCount);
        Assert.Equal(0, controller.CancelCount);
        Assert.Empty(controller.Actions);
        Assert.False(viewModel.NewTabCommand.CanExecute(null));
        Assert.False(viewModel.TakeOverCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void PresenterFactoryFailure_IsVisibleWithoutRepeatedCreationOnStatusUpdates()
    {
        var calls = 0;
        using var viewModel = Create(out var controller, pagePresenterFactory: _ =>
        {
            calls++;
            throw new PlatformNotSupportedException("Windows is required.");
        });
        var sessionId = new SessionId("sess_active");
        controller.Status = TabStatus("first");
        viewModel.Bind(sessionId);
        viewModel.AttachView();
        controller.RaiseStatusChanged(sessionId, TabStatus("first"));

        Assert.Equal(1, calls);
        Assert.Contains("Windows is required", viewModel.Banner);
        Assert.Contains("Windows is required", viewModel.EmptyStateText);
        Assert.True(viewModel.ShowEmptyState);
        Assert.False(viewModel.HasPagePresenter);
    }

    [AvaloniaFact]
    public async Task TabAndNavigationCommands_AreUserInitiatedAndTargetCorrectTabs()
    {
        using var viewModel = Create(out var controller);
        var sessionId = new SessionId("sess_active");
        controller.Status = TabStatus("first") with { IsUserControlled = true };
        viewModel.Bind(sessionId);
        Assert.False(viewModel.SelectTabCommand.CanExecute(viewModel.Tabs[0]));
        Assert.False(viewModel.CloseTabCommand.CanExecute(new BrowserTabStatus("missing", "", "")));

        await viewModel.BackCommand.ExecuteAsync(null);
        await viewModel.ForwardCommand.ExecuteAsync(null);
        await viewModel.ReloadCommand.ExecuteAsync(null);
        await viewModel.SelectTabCommand.ExecuteAsync(viewModel.Tabs[1]);
        await viewModel.CloseTabCommand.ExecuteAsync(viewModel.Tabs[1]);
        await viewModel.NewTabCommand.ExecuteAsync(null);
        viewModel.Address = "about:blank";
        await viewModel.NavigateCommand.ExecuteAsync(null);

        Assert.Equal(["back", "forward", "reload", "select_tab", "close_tab", "new_tab", "navigate"],
            controller.Actions.Select(request => request.Action.Verb));
        Assert.Equal(["first", "first", "first", "second", "second", null, "first"],
            controller.Actions.Select(request => request.Action.TabId));
        Assert.All(controller.Actions, request =>
        {
            Assert.Equal(sessionId, request.SessionId);
            Assert.True(request.Action.UserInitiated);
        });
        Assert.All(controller.Actions.Take(6), request =>
            Assert.Equal(System.Text.Json.JsonValueKind.Object, Assert.IsType<BrowserCommandAction>(request.Action).Input.ValueKind));
        Assert.True(viewModel.IsUserControlled);
        Assert.DoesNotContain(controller.Actions, request => request.Action.Verb == "resume");
    }

    [AvaloniaFact]
    public async Task TakeOver_RemainsAvailableWhileAgentBrowserActionIsBusy()
    {
        using var viewModel = Create(out var controller);
        var sessionId = new SessionId("sess_active");
        controller.Status = TabStatus("first") with { State = BrowserSessionState.Busy };
        viewModel.Bind(sessionId);

        Assert.Equal("Busy", viewModel.StateText);
        Assert.True(viewModel.TakeOverCommand.CanExecute(null));
        await viewModel.TakeOverCommand.ExecuteAsync(null);

        Assert.Equal([sessionId], controller.Takeovers);
    }

    [AvaloniaFact]
    public async Task TakeOverAndResume_RequireExplicitOwnershipChanges()
    {
        using var viewModel = Create(out var controller);
        var sessionId = new SessionId("sess_active");
        controller.Status = TabStatus("first");
        viewModel.Bind(sessionId);
        Assert.False(viewModel.ResumeCommand.CanExecute(null));
        await viewModel.TakeOverCommand.ExecuteAsync(null);
        Assert.Equal([sessionId], controller.Takeovers);

        controller.Status = controller.Status with { IsUserControlled = true };
        controller.RaiseStatusChanged(sessionId, controller.Status);
        Assert.False(viewModel.TakeOverCommand.CanExecute(null));
        Assert.True(viewModel.ResumeCommand.CanExecute(null));
        await viewModel.ResumeCommand.ExecuteAsync(null);

        var request = Assert.Single(controller.Actions);
        Assert.Equal("resume", request.Action.Verb);
        Assert.True(request.Action.UserInitiated);
        Assert.True(viewModel.IsUserControlled);
        controller.RaiseStatusChanged(sessionId, controller.Status with { IsUserControlled = false });
        Assert.False(viewModel.IsUserControlled);
        Assert.True(viewModel.TakeOverCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void QueuedStatus_IsIgnoredAfterSessionSwitchEvenWhenSwitchingBack()
    {
        var controller = new FakeBrowserController();
        var scheduler = new QueuedUiScheduler();
        using var viewModel = new BrowserPaneViewModel(controller, scheduler);
        var sessionId = new SessionId("sess_active");
        viewModel.Bind(sessionId);
        controller.RaiseStatusChanged(sessionId, TabStatus("first"));
        viewModel.Bind(Other);
        viewModel.Bind(sessionId);
        scheduler.Run();

        Assert.Null(viewModel.ActiveTabId);
        Assert.Empty(viewModel.Tabs);
        Assert.Equal("Idle", viewModel.StateText);

        controller.RaiseStatusChanged(sessionId, TabStatus("second"));
        viewModel.Dispose();
        scheduler.Run();
        Assert.Empty(viewModel.Tabs);
    }

    [AvaloniaFact]
    public async Task CommandCompletion_FromPreviousBindingCannotOverwriteNewBinding()
    {
        using var viewModel = Create(out var controller);
        var completion = new TaskCompletionSource<BrowserActionResult>();
        controller.ExecuteHandler = (_, _) => completion.Task;
        var sessionId = new SessionId("sess_active");
        viewModel.Bind(sessionId);
        viewModel.Address = "example.com";
        var navigation = viewModel.NavigateCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsNavigating);
        viewModel.Bind(Other);
        viewModel.Bind(sessionId);
        viewModel.Banner = "Current binding";

        completion.SetResult(new BrowserActionResult(false, Error: "Stale failure"));
        await navigation;

        Assert.Equal("Current binding", viewModel.Banner);
        Assert.False(viewModel.IsNavigating);
    }

    [AvaloniaFact]
    public async Task CommandExceptions_AreShownAndDoNotLeaveBusyState()
    {
        using var viewModel = Create(out var controller);
        controller.Status = TabStatus("first");
        viewModel.Bind(new SessionId("sess_active"));
        controller.ExecuteHandler = (_, _) => throw new InvalidOperationException("Page closed.");

        await viewModel.ReloadCommand.ExecuteAsync(null);

        Assert.Equal("Reload failed: Page closed.", viewModel.Banner);
        Assert.False(viewModel.IsNavigating);
        Assert.True(viewModel.ReloadCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task BrowserPaneView_EmbedsPresenterAndExposesTabNavigationAndOwnershipControls()
    {
        var image = new FakeImage();
        var presenters = new List<FakePresenter>();
        using var viewModel = Create(out var controller, _ => image, id =>
        {
            var presenter = new FakePresenter(id);
            presenters.Add(presenter);
            return presenter;
        });
        var sessionId = new SessionId("sess_active");
        controller.Status = TabStatus("first") with { LatestScreenshotUri = @"C:\shots\active.png" };
        viewModel.Bind(sessionId);
        var view = new BrowserPaneView { DataContext = viewModel };
        Assert.Empty(presenters);
        var window = new Window { Width = 420, Height = 600, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var page = view.FindControl<ContentControl>("PART_BrowserPage")!;
            Assert.Same(presenters[0], page.Content);
            Assert.Contains(presenters[0], view.GetVisualDescendants());
            Assert.True(page.Bounds.Height > 250);
            Assert.False(view.FindControl<Image>("PART_BrowserScreenshot")!.IsVisible);
            Assert.Equal(2, view.FindControl<ItemsControl>("PART_BrowserTabs")!.ItemCount);
            await ClickAsync(view.FindControl<Button>("PART_BrowserBack")!);
            await ClickAsync(view.FindControl<Button>("PART_BrowserForward")!);
            await ClickAsync(view.FindControl<Button>("PART_BrowserRefresh")!);
            var secondTab = view.GetVisualDescendants().OfType<Button>()
                .Single(button => button.DataContext is BrowserTabStatus { Id: "second" } && button.Classes.Contains("ghost"));
            await ClickAsync(secondTab);
            var closeTab = view.GetVisualDescendants().OfType<Button>()
                .Single(button => button.DataContext is BrowserTabStatus { Id: "second" } && button.Classes.Contains("tiny"));
            await ClickAsync(closeTab);
            await ClickAsync(view.FindControl<Button>("PART_BrowserNewTab")!);
            Assert.Equal(["back", "forward", "reload", "select_tab", "close_tab", "new_tab"],
                controller.Actions.Select(request => request.Action.Verb));

            var takeOver = view.FindControl<Button>("PART_BrowserTakeOver")!;
            var resume = view.FindControl<Button>("PART_BrowserResume")!;
            Assert.True(takeOver.IsVisible);
            Assert.False(resume.IsVisible);
            await ClickAsync(takeOver);
            Assert.Single(controller.Takeovers);
            controller.Status = controller.Status with { IsUserControlled = true };
            controller.RaiseStatusChanged(sessionId, controller.Status);
            Dispatcher.UIThread.RunJobs();
            Assert.False(takeOver.IsVisible);
            Assert.True(resume.IsVisible);
            await ClickAsync(resume);
            Assert.Equal("resume", controller.Actions[^1].Action.Verb);

            view.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, presenters[0].DisposeCount);
            view.IsVisible = true;
        }
        finally
        {
            window.Close();
        }
        Assert.Equal(1, presenters[0].DisposeCount);
        Assert.Equal(0, controller.DisposeCount);
    }

    [AvaloniaFact]
    public void BrowserPaneView_DataContextChangeAndDetachReleaseOnlyPresenterWrappers()
    {
        var presenters = new List<FakePresenter>();
        using var root = Create(out var controller, pagePresenterFactory: id =>
        {
            var presenter = new FakePresenter(id);
            presenters.Add(presenter);
            return presenter;
        });
        controller.Status = TabStatus("first");
        using var first = root.CreateBoundView(new SessionId("sess_first"));
        using var second = root.CreateBoundView(new SessionId("sess_second"));
        var view = new BrowserPaneView { DataContext = first };
        var window = new Window { Width = 420, Height = 600, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            view.DataContext = second;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(first.PagePresenter);
            Assert.Equal(1, presenters[0].DisposeCount);
            Assert.Same(presenters[1], second.PagePresenter);
            second.Dispose();
            Assert.Equal(1, presenters[1].DisposeCount);
            Assert.Null(view.FindControl<ContentControl>("PART_BrowserPage")!.Content);
        }
        finally
        {
            window.Close();
        }
        Assert.Equal(1, presenters[1].DisposeCount);
        Assert.Equal(0, controller.DisposeCount);
        Assert.Empty(controller.Actions);
    }

    [AvaloniaFact]
    public void BrowserPaneView_ReattachesSameInstanceOnlyAfterViewportIsRooted()
    {
        var presenters = new List<FakePresenter>();
        BrowserPaneView? view = null;
        Window? window = null;
        using var viewModel = Create(out var controller, pagePresenterFactory: id =>
        {
            Assert.NotNull(view);
            Assert.NotNull(window);
            Assert.Same(window, TopLevel.GetTopLevel(view.FindControl<ContentControl>("PART_BrowserPage")!));
            var presenter = new FakePresenter(id);
            presenters.Add(presenter);
            return presenter;
        });
        controller.Status = TabStatus("first");
        viewModel.Bind(new SessionId("sess_active"));
        view = new BrowserPaneView { DataContext = viewModel };
        window = new Window { Width = 420, Height = 600 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            for (var iteration = 0; iteration < 3; iteration++)
            {
                window.Content = view;
                Assert.Equal(iteration, presenters.Count);
                Assert.Null(viewModel.PagePresenter);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.Equal(iteration + 1, presenters.Count);
                Assert.Same(presenters[iteration], viewModel.PagePresenter);
                Assert.Contains(presenters[iteration], view.GetVisualDescendants());

                window.Content = null;
                Assert.Null(viewModel.PagePresenter);
                Assert.Null(view.FindControl<ContentControl>("PART_BrowserPage")!.Content);
                Assert.Equal(1, presenters[iteration].DisposeCount);
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            window.Close();
        }
        Assert.All(presenters, presenter => Assert.Equal(1, presenter.DisposeCount));
        Assert.Equal(0, controller.DisposeCount);
        Assert.Equal(0, controller.CancelCount);
    }

    [AvaloniaFact]
    public void BrowserPaneView_PendingAttachmentIsCancelledByDetachOrDataContextChange()
    {
        var presenters = new List<FakePresenter>();
        using var root = Create(out var controller, pagePresenterFactory: id =>
        {
            var presenter = new FakePresenter(id);
            presenters.Add(presenter);
            return presenter;
        });
        controller.Status = TabStatus("first");
        using var first = root.CreateBoundView(new SessionId("sess_first"));
        using var second = root.CreateBoundView(new SessionId("sess_second"));
        var view = new BrowserPaneView { DataContext = first };
        var window = new Window { Width = 420, Height = 600 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.Content = view;
            view.DataContext = second;
            window.Content = null;
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(presenters);
            Assert.Null(first.PagePresenter);
            Assert.Null(second.PagePresenter);

            window.Content = view;
            view.DataContext = first;
            Dispatcher.UIThread.RunJobs();
            Assert.Single(presenters);
            Assert.Same(presenters[0], first.PagePresenter);
            Assert.Null(second.PagePresenter);
            view.DataContext = second;
            Assert.Null(first.PagePresenter);
            Assert.Equal(1, presenters[0].DisposeCount);
            view.DataContext = first;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, presenters.Count);
            Assert.Same(presenters[1], first.PagePresenter);
            Assert.Null(second.PagePresenter);
        }
        finally
        {
            window.Close();
        }
        Assert.All(presenters, presenter => Assert.Equal(1, presenter.DisposeCount));
        Assert.Equal(0, controller.DisposeCount);
    }

    private static async Task ClickAsync(Button button)
    {
        Dispatcher.UIThread.RunJobs();
        var window = Assert.IsType<Window>(TopLevel.GetTopLevel(button));
        window.UpdateLayout();
        var visual = ElementComposition.GetElementVisual(window)!;
        var rendered = visual.Compositor.RequestCompositionBatchCommitAsync().Rendered;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await rendered.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();
        Assert.True(button.IsEffectivelyVisible);
        Assert.True(button.IsEffectivelyEnabled);
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(point));
        Assert.True(ReferenceEquals(hit, button) || hit.GetVisualAncestors().Contains(button),
            $"Expected {button.Name} at {point}, hit {hit.GetType().Name} {hit.Bounds}; button bounds {button.Bounds}.");
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static BrowserSessionStatus TabStatus(string activeId) => new(BrowserSessionState.Launched,
        Tabs:
        [
            new BrowserTabStatus("first", "https://example.com/first", "First"),
            new BrowserTabStatus("second", "https://example.com/second", "Second"),
        ], ActiveTabId: activeId);

    private static BrowserPaneViewModel Create(
        out FakeBrowserController controller, Func<string, IImage?>? imageLoader = null,
        Func<string, Control>? pagePresenterFactory = null)
    {
        controller = new FakeBrowserController();
        return new BrowserPaneViewModel(controller, InlineUiScheduler.Instance, imageLoader, pagePresenterFactory);
    }

    private sealed class FakePresenter(string tabId) : Control, IDisposable
    {
        public string TabId { get; } = tabId;

        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class QueuedUiScheduler : IUiScheduler
    {
        private readonly Queue<Action> _actions = [];

        public void Post(Action action) => _actions.Enqueue(action);

        public void Run()
        {
            while (_actions.TryDequeue(out var action))
            {
                action();
            }
        }
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

        public Func<SessionId, BrowserAction, Task<BrowserActionResult>>? ExecuteHandler { get; set; }

        public int DisposeCount { get; private set; }

        public int CancelCount { get; private set; }

        public event EventHandler<BrowserStatusChangedEventArgs>? StatusChanged;

        public void RaiseStatusChanged(SessionId sessionId, BrowserSessionStatus status) =>
            StatusChanged?.Invoke(this, new BrowserStatusChangedEventArgs(sessionId, status));

        public Task<BrowserActionResult> ExecuteAsync(SessionId sessionId, BrowserAction action, CancellationToken cancellationToken)
        {
            Actions.Add((sessionId, action));
            return ExecuteHandler?.Invoke(sessionId, action) ?? Task.FromResult(Result);
        }

        public Task CancelAsync(SessionId sessionId, CancellationToken cancellationToken)
        {
            CancelCount++;
            return Task.CompletedTask;
        }

        public Task TakeoverAsync(SessionId sessionId, CancellationToken cancellationToken)
        {
            Takeovers.Add(sessionId);
            return Task.CompletedTask;
        }

        public BrowserSessionStatus? GetStatus(SessionId sessionId) => Status;

        public void Dispose() => DisposeCount++;
    }
}
