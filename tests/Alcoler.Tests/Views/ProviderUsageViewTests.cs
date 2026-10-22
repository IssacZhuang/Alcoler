using Alcoler.Adapters.ProviderUsage;
using Alcoler.Controls;
using Alcoler.ViewModel;
using Alcoler.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Assert = Xunit.Assert;

namespace Alcoler.Tests.Views;

public sealed class ProviderUsageViewTests
{
    [AvaloniaFact]
    public async Task View_UsesCompactRefreshAndRemainingQuotaGridWithoutRepeatedQueries()
    {
        var service = new StubService(id => Quota(id));
        using var viewModel = new ProviderUsageViewModel(service);
        viewModel.Configure("p", "https://api.z.ai", "test-only-key");
        var view = new ProviderUsageView { DataContext = viewModel };
        var window = CreateWindow(view);
        try
        {
            Assert.Equal(0, service.Calls);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, service.Calls);
            var refresh = view.FindControl<Button>("PART_UsageRefresh")!;
            Assert.Equal(28, refresh.Width);
            Assert.Equal(28, refresh.Height);
            Assert.Single(refresh.GetVisualDescendants().OfType<Icon>());
            var columns = Assert.Single(view.GetVisualDescendants().OfType<UniformGrid>());
            Assert.Equal(3, columns.Columns);
            var meters = view.GetVisualDescendants().OfType<ProgressBar>().ToArray();
            Assert.Equal(new[] { 75d, 60d, 45d }, meters.Select(meter => meter.Value));
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Usage");
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "75%");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, service.Calls);

            await ClickAsync(window, refresh);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, service.Calls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void HiddenHost_DoesNotQueryUntilOpened_AndReopeningRefreshesOnce()
    {
        var service = new StubService(Quota);
        using var viewModel = new ProviderUsageViewModel(service);
        viewModel.Configure("p", "https://api.z.ai", "test-only-key");
        var view = new ProviderUsageView { DataContext = viewModel };
        var host = new ContentControl { Content = view, IsVisible = false };
        var window = CreateWindow(host);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, service.Calls);
            host.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, service.Calls);
            host.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            host.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, service.Calls);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, service.Calls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DetachReattach_RefreshesWithoutDisposingOwnerViewModel()
    {
        var service = new StubService(Quota);
        using var viewModel = new ProviderUsageViewModel(service);
        viewModel.Configure("p", "https://api.z.ai", "test-only-key");
        var view = new ProviderUsageView { DataContext = viewModel };
        var host = new ContentControl { Content = view };
        var window = CreateWindow(host);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, service.Calls);
            host.Content = null;
            Dispatcher.UIThread.RunJobs();
            Assert.True(viewModel.IsVisible);
            host.Content = view;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, service.Calls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void VisibleProviderEdits_ClearUiWithoutFetching_AndMissingCredentialsHideSection()
    {
        var service = new StubService(Quota);
        using var viewModel = new ProviderUsageViewModel(service);
        viewModel.Configure("p", "https://api.z.ai", "test-only-key");
        var view = new ProviderUsageView { DataContext = viewModel };
        var window = CreateWindow(view);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<ItemsControl>("PART_UsageQuota")!.IsVisible);
            viewModel.Configure("p", "https://api.z.ai", "edited-test-key");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, service.Calls);
            Assert.False(view.FindControl<ItemsControl>("PART_UsageQuota")!.IsVisible);
            Assert.True(view.FindControl<StackPanel>("PART_UsageSection")!.IsVisible);
            viewModel.Configure("p", "https://api.z.ai", "");
            Dispatcher.UIThread.RunJobs();
            Assert.False(view.FindControl<StackPanel>("PART_UsageSection")!.IsVisible);
            Assert.Equal(1, service.Calls);
            viewModel.Configure("p", "https://api.z.ai.attacker.example", "test-key");
            Dispatcher.UIThread.RunJobs();
            Assert.False(view.FindControl<StackPanel>("PART_UsageSection")!.IsVisible);
            Assert.Equal(1, service.Calls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task FailedQuery_ShowsSafeRetry_ThenBalanceInSameView()
    {
        var fail = true;
        var service = new StubService(id => fail
            ? new ProviderUsageSnapshot(id, ProviderUsageAdapterId.OpenRouter, ProviderUsageKind.Balance, false,
                [], DateTimeOffset.UtcNow, new ProviderUsageError(ProviderUsageErrorCode.AuthFailed, "never-display-secret"))
            : new ProviderUsageSnapshot(id, ProviderUsageAdapterId.OpenRouter, ProviderUsageKind.Balance, true,
                [new ProviderUsageTier(ProviderUsageTierId.Balance, Remaining: 25, Unit: "USD")], DateTimeOffset.UtcNow));
        using var viewModel = new ProviderUsageViewModel(service);
        viewModel.Configure("p", "https://openrouter.ai", "test-only-key");
        var view = new ProviderUsageView { DataContext = viewModel };
        var window = CreateWindow(view);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(viewModel.HasError);
            Assert.DoesNotContain("secret", view.FindControl<TextBlock>("PART_UsageError")!.Text!);
            Assert.False(view.FindControl<StackPanel>("PART_UsageBalance")!.IsVisible);
            fail = false;
            var retry = view.FindControl<Button>("PART_UsageRetry")!;
            Assert.Same(viewModel.RefreshCommand, retry.Command);
            await ClickAsync(window, retry);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, service.Calls);
            Assert.False(viewModel.HasError);
            Assert.True(view.FindControl<StackPanel>("PART_UsageBalance")!.IsVisible);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.EndsWith(" USD") == true);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task MountAndExplicitOpenRefresh_ShareRequestAndDisableButtonWhileLoading()
    {
        var pending = new TaskCompletionSource<ProviderUsageSnapshot>();
        var service = new PendingService(pending.Task);
        using var viewModel = new ProviderUsageViewModel(service);
        viewModel.Configure("p", "https://api.z.ai", "test-only-key");
        var view = new ProviderUsageView { DataContext = viewModel };
        var window = CreateWindow(view);
        try
        {
            var explicitOpen = viewModel.RefreshAsync();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, service.Calls);
            Assert.True(view.FindControl<TextBlock>("PART_UsageLoading")!.IsVisible);
            Assert.Same(viewModel.RefreshCommand, view.FindControl<Button>("PART_UsageRefresh")!.Command);
            Assert.False(view.FindControl<Button>("PART_UsageRefresh")!.IsEffectivelyEnabled);
            pending.SetResult(Quota("p"));
            await explicitOpen;
            Dispatcher.UIThread.RunJobs();
            Assert.False(view.FindControl<TextBlock>("PART_UsageLoading")!.IsVisible);
            Assert.True(view.FindControl<Button>("PART_UsageRefresh")!.IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TwoTierQuota_MetersStayInsideTheirColumns()
    {
        var service = new StubService(id => new ProviderUsageSnapshot(id,
            ProviderUsageAdapterId.Zhipu, ProviderUsageKind.CodingPlan, true,
            [
                new ProviderUsageTier(ProviderUsageTierId.FiveHour, UsedPercentage: 4, ResetsAt: DateTimeOffset.Now.AddHours(1)),
                new ProviderUsageTier(ProviderUsageTierId.Weekly, UsedPercentage: 37, ResetsAt: DateTimeOffset.Now.AddDays(3)),
            ], DateTimeOffset.UtcNow));
        using var viewModel = new ProviderUsageViewModel(service);
        viewModel.Configure("p", "https://api.z.ai", "test-only-key");
        var view = new ProviderUsageView { DataContext = viewModel };
        var window = CreateWindow(view);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var meters = view.GetVisualDescendants().OfType<ProgressBar>().ToArray();
            Assert.Equal(2, meters.Length);
            var left = meters[0].TranslatePoint(default, view)!.Value;
            var right = meters[1].TranslatePoint(default, view)!.Value;
            Assert.True(meters[0].Bounds.Width > 100);
            Assert.True(meters[1].Bounds.Width > 100);
            Assert.True(left.X + meters[0].Bounds.Width <= right.X);
        }
        finally
        {
            window.Close();
        }
    }

    private static Window CreateWindow(Control content) => new()
    {
        Width = 320,
        Height = 240,
        SizeToContent = SizeToContent.Manual,
        Content = content,
    };

    private static async Task ClickAsync(Window window, Button button)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        // Hit testing reads the composed scene; layout and a timer tick alone do not finish a throttled commit.
        var visual = ElementComposition.GetElementVisual(window);
        Assert.NotNull(visual);
        var rendered = visual.Compositor.RequestCompositionBatchCommitAsync().Rendered;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await rendered.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();
        Assert.True(button.IsEffectivelyVisible);
        Assert.True(button.IsEffectivelyEnabled);
        Assert.True(button.Bounds.Width > 0 && button.Bounds.Height > 0);
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        Assert.True(new Rect(window.ClientSize).Contains(point),
            $"Expected {button.Name} at {point} inside client size {window.ClientSize}.");
        var transformed = button.GetTransformedBounds();
        Assert.True(transformed is { } bounds && bounds.Clip.Contains(point),
            $"Expected {button.Name} at {point} inside ancestor clip {transformed?.Clip}.");
        var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(point));
        Assert.True(ReferenceEquals(hit, button) || hit.GetVisualAncestors().Contains(button),
            $"Expected {button.Name} at {point}, hit {hit.GetType().Name} {hit.Bounds}; client size {window.ClientSize}.");
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private static ProviderUsageSnapshot Quota(string id) => new(id,
        ProviderUsageAdapterId.Zhipu, ProviderUsageKind.CodingPlan, true,
        [
            new ProviderUsageTier(ProviderUsageTierId.FiveHour, UsedPercentage: 25, ResetsAt: DateTimeOffset.Now.AddHours(1)),
            new ProviderUsageTier(ProviderUsageTierId.Weekly, UsedPercentage: 40, ResetsAt: DateTimeOffset.Now.AddDays(3)),
            new ProviderUsageTier(ProviderUsageTierId.Monthly, UsedPercentage: 55, ResetsAt: DateTimeOffset.Now.AddDays(15)),
        ], DateTimeOffset.UtcNow);

    private sealed class StubService(Func<string, ProviderUsageSnapshot> query) : IProviderUsageService
    {
        public int Calls { get; private set; }

        public Task<ProviderUsageSnapshot> GetUsageAsync(string providerId, string? baseUrl, string? apiKey,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(query(providerId));
        }
    }

    private sealed class PendingService(Task<ProviderUsageSnapshot> pending) : IProviderUsageService
    {
        public int Calls { get; private set; }

        public Task<ProviderUsageSnapshot> GetUsageAsync(string providerId, string? baseUrl, string? apiKey,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return pending;
        }
    }
}
