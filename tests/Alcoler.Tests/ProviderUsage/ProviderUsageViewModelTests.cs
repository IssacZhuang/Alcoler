using Alcoler.Adapters.ProviderUsage;
using Alcoler.ViewModel;
using Xunit;

namespace Alcoler.Tests.ProviderUsage;

public sealed class ProviderUsageViewModelTests
{
    [Theory]
    [InlineData("https://api.deepseek.com", null, true, false)]
    [InlineData("https://api.deepseek.com", " ", true, false)]
    [InlineData("https://api.deepseek.com.attacker.example", "test-key", false, false)]
    [InlineData("https://api.deepseek.com", "test-key", true, true)]
    public async Task Configure_DetectsVisibilityWithoutFetching(string url, string? key, bool supported, bool visible)
    {
        var service = new ControlledService();
        using var viewModel = new ProviderUsageViewModel(service);

        viewModel.Configure("p", url, key);

        Assert.Equal(supported, viewModel.IsSupported);
        Assert.Equal(visible, viewModel.IsVisible);
        Assert.Equal(visible, viewModel.RefreshCommand.CanExecute(null));
        Assert.Empty(service.Requests);
        if (!visible)
        {
            await viewModel.RefreshAsync();
            Assert.Empty(service.Requests);
        }
    }

    [Fact]
    public async Task Refresh_TracksLoadingAndShowsRemainingRatherThanUsed()
    {
        var service = new ControlledService();
        using var viewModel = new ProviderUsageViewModel(service);
        viewModel.Configure("p", "https://api.kimi.com", "test-key");
        var refresh = viewModel.RefreshAsync();

        Assert.True(viewModel.IsLoading);
        Assert.False(viewModel.CanRefresh);
        Assert.False(viewModel.RefreshCommand.CanExecute(null));
        service.Complete(0, Quota("p", new ProviderUsageTier(ProviderUsageTierId.FiveHour, UsedPercentage: 25)));
        await refresh;

        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.CanRefresh);
        Assert.True(viewModel.ShowQuota);
        Assert.False(viewModel.ShowBalance);
        var tier = Assert.Single(viewModel.QuotaTiers);
        Assert.Equal(75d, tier.RemainingPercentage);
        Assert.Equal(75d, tier.MeterValue);
        Assert.Equal("75%", tier.PercentageText);
    }

    [Fact]
    public async Task ConcurrentRefresh_CoalescesRequestsAndAllowsExplicitRetryAfterCompletion()
    {
        var service = new ControlledService();
        using var viewModel = new ProviderUsageViewModel(service);
        viewModel.Configure("p", "https://api.kimi.com", "test-key");
        var first = viewModel.RefreshAsync();
        var second = viewModel.RefreshAsync();
        var third = viewModel.RefreshAsync();

        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.Single(service.Requests);
        service.Complete(0, Quota("p"));
        await Task.WhenAll(first, second, third);
        var next = viewModel.RefreshAsync();
        Assert.Equal(2, service.Requests.Count);
        service.Complete(1, Quota("p"));
        await next;
    }

    [Fact]
    public async Task ProviderSwitch_CancelsAndIgnoresOldResponseEvenIfTransportIgnoresCancellation()
    {
        var service = new ControlledService();
        using var viewModel = new ProviderUsageViewModel(service);
        viewModel.Configure("old", "https://api.kimi.com", "old-test-key");
        var oldRefresh = viewModel.RefreshAsync();
        viewModel.Configure("new", "https://api.deepseek.com", "new-test-key");

        Assert.True(service.Requests[0].Token.IsCancellationRequested);
        Assert.Null(viewModel.Snapshot);
        Assert.Empty(viewModel.QuotaTiers);
        Assert.False(viewModel.IsLoading);
        Assert.Single(service.Requests);
        var newRefresh = viewModel.RefreshAsync();
        service.Complete(0, Quota("old", new ProviderUsageTier(ProviderUsageTierId.Weekly, UsedPercentage: 99)));
        await oldRefresh;
        Assert.Null(viewModel.Snapshot);
        Assert.True(viewModel.IsLoading);

        service.Complete(1, Balance("new", 19.5));
        await newRefresh;
        Assert.Equal("new", viewModel.Snapshot?.ProviderId);
        Assert.True(viewModel.ShowBalance);
        Assert.Empty(viewModel.QuotaTiers);
        Assert.Equal(19.5, viewModel.Snapshot?.Tiers[0].Remaining);
    }

    [Theory]
    [InlineData("https://api.kimi.com", "changed-key")]
    [InlineData("https://api.kimi.com/coding/v1", "test-key")]
    [InlineData("https://api.deepseek.com", "test-key")]
    [InlineData("https://unsupported.example", "test-key")]
    [InlineData("https://api.kimi.com", "")]
    public async Task ProviderEdit_InvalidatesLoadedAndInFlightResultsWithoutAutomaticQuery(string nextUrl, string nextKey)
    {
        var service = new ControlledService();
        using var viewModel = new ProviderUsageViewModel(service);
        viewModel.Configure("p", "https://api.kimi.com", "test-key");
        var loaded = viewModel.RefreshAsync();
        service.Complete(0, Quota("p", new ProviderUsageTier(ProviderUsageTierId.FiveHour, UsedPercentage: 10)));
        await loaded;
        var inFlight = viewModel.RefreshAsync();

        viewModel.Configure("p", nextUrl, nextKey);
        Assert.Null(viewModel.Snapshot);
        Assert.False(viewModel.HasData);
        Assert.False(viewModel.IsLoading);
        Assert.Empty(viewModel.QuotaTiers);
        Assert.True(service.Requests[1].Token.IsCancellationRequested);
        service.Complete(1, Quota("p", new ProviderUsageTier(ProviderUsageTierId.FiveHour, UsedPercentage: 99)));
        await inFlight;

        Assert.Null(viewModel.Snapshot);
        Assert.Equal(2, service.Requests.Count);
    }

    [Fact]
    public async Task SameConfiguration_PreservesSnapshotAndInFlightRequest()
    {
        var service = new ControlledService();
        using var viewModel = new ProviderUsageViewModel(service);
        viewModel.Configure("p", "https://api.kimi.com", "test-key");
        var refresh = viewModel.RefreshAsync();
        viewModel.Configure("p", "https://api.kimi.com", "test-key");

        Assert.False(service.Requests[0].Token.IsCancellationRequested);
        Assert.True(viewModel.IsLoading);
        var snapshot = Quota("p");
        service.Complete(0, snapshot);
        await refresh;
        viewModel.Configure("p", "https://api.kimi.com", "test-key");
        Assert.Same(snapshot, viewModel.Snapshot);
        Assert.Single(service.Requests);
    }

    [Fact]
    public async Task ErrorAndRetry_ExposeSafeMessageAndReplaceOldQuota()
    {
        var service = new ControlledService();
        using var viewModel = new ProviderUsageViewModel(service);
        viewModel.Configure("p", "https://api.kimi.com", "test-key");
        var first = viewModel.RefreshAsync();
        service.Complete(0, Quota("p", new ProviderUsageTier(ProviderUsageTierId.Monthly, UsedPercentage: 50)));
        await first;
        var error = viewModel.RefreshAsync();
        service.Complete(1, Quota("p") with
        {
            CredentialValid = false,
            Error = new ProviderUsageError(ProviderUsageErrorCode.AuthFailed, "secret-do-not-display"),
        });
        await error;

        Assert.True(viewModel.HasError);
        Assert.False(viewModel.HasData);
        Assert.False(viewModel.ShowQuota);
        Assert.Empty(viewModel.QuotaTiers);
        Assert.Contains("Authentication failed", viewModel.ErrorMessage);
        Assert.DoesNotContain("secret", viewModel.ErrorMessage);
        var retry = viewModel.RefreshCommand.ExecuteAsync(null);
        service.Complete(2, Quota("p", new ProviderUsageTier(ProviderUsageTierId.Weekly, UsedPercentage: 20)));
        await retry;
        Assert.False(viewModel.HasError);
        Assert.Equal(80d, Assert.Single(viewModel.QuotaTiers).RemainingPercentage);
    }

    [Fact]
    public async Task ServiceException_IsSafeAndRetryable()
    {
        var service = new ControlledService();
        using var viewModel = new ProviderUsageViewModel(service);
        viewModel.Configure("p", "https://api.kimi.com", "test-key");
        var refresh = viewModel.RefreshAsync();
        service.Requests[0].Completion.SetException(new InvalidOperationException("secret-do-not-display"));
        await refresh;

        Assert.True(viewModel.HasError);
        Assert.Equal(ProviderUsageErrorCode.Unknown, viewModel.Snapshot?.Error?.Code);
        Assert.DoesNotContain("secret", viewModel.ErrorMessage);
        Assert.DoesNotContain("secret", viewModel.Snapshot!.ToString());
        Assert.True(viewModel.CanRefresh);
    }

    [Fact]
    public async Task CallerCancellation_DoesNotPublishAnErrorOrStaleData()
    {
        var service = new ControlledService();
        using var viewModel = new ProviderUsageViewModel(service);
        viewModel.Configure("p", "https://api.kimi.com", "test-key");
        using var cancellation = new CancellationTokenSource();
        var refresh = viewModel.RefreshAsync(cancellation.Token);
        cancellation.Cancel();
        service.Complete(0, Quota("p", new ProviderUsageTier(ProviderUsageTierId.FiveHour, UsedPercentage: 90)));
        await refresh;

        Assert.Null(viewModel.Snapshot);
        Assert.False(viewModel.HasError);
        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.CanRefresh);
    }

    [Fact]
    public async Task Dispose_CancelsPendingAndDropsCredentialsAndResults()
    {
        var service = new ControlledService();
        var viewModel = new ProviderUsageViewModel(service);
        viewModel.Configure("p", "https://api.kimi.com", "test-key");
        var refresh = viewModel.RefreshAsync();
        viewModel.Dispose();
        viewModel.Dispose();
        Assert.True(service.Requests[0].Token.IsCancellationRequested);
        service.Complete(0, Quota("p"));
        await refresh;
        await viewModel.RefreshAsync();

        Assert.False(viewModel.IsVisible);
        Assert.False(viewModel.IsLoading);
        Assert.Null(viewModel.Snapshot);
        Assert.Single(service.Requests);
    }

    [Fact]
    public async Task BalanceAndThreeWindowQuota_UseTheSameViewModelSurface()
    {
        var service = new ControlledService();
        using var viewModel = new ProviderUsageViewModel(service);
        viewModel.Configure("balance", "https://api.deepseek.com", "test-key");
        var balance = viewModel.RefreshAsync();
        service.Complete(0, Balance("balance", 12.5) with
        {
            Tiers = [new ProviderUsageTier(ProviderUsageTierId.Balance, Remaining: 12.5, Total: 20, Used: 7.5, Unit: "USD")],
        });
        await balance;
        Assert.True(viewModel.ShowBalance);
        Assert.EndsWith(" USD", viewModel.BalanceText);
        Assert.True(viewModel.HasBalanceDetail);
        Assert.Contains("used /", viewModel.BalanceDetail);
        Assert.Equal(1, viewModel.QuotaColumns);

        viewModel.Configure("quota", "https://api.z.ai", "test-key");
        var quota = viewModel.RefreshAsync();
        service.Complete(1, Quota("quota",
            new ProviderUsageTier(ProviderUsageTierId.FiveHour, UsedPercentage: 10),
            new ProviderUsageTier(ProviderUsageTierId.Weekly, UsedPercentage: 20),
            new ProviderUsageTier(ProviderUsageTierId.Monthly, UsedPercentage: 30)));
        await quota;
        Assert.True(viewModel.ShowQuota);
        Assert.Equal(3, viewModel.QuotaColumns);
        Assert.Equal(new[] { 90d, 80d, 70d }, viewModel.QuotaTiers.Select(tier => tier.MeterValue));
    }

    [Theory]
    [InlineData(-10, 100)]
    [InlineData(110, 0)]
    [InlineData(90.5, 9.5)]
    public void QuotaPercent_IsRemainingAndClamped(double used, double remaining)
    {
        var tier = new ProviderUsageTierViewModel(new ProviderUsageTier(ProviderUsageTierId.FiveHour, UsedPercentage: used));

        Assert.Equal(remaining, tier.RemainingPercentage);
        Assert.Equal(remaining, tier.MeterValue);
    }

    [Fact]
    public void QuotaFallbackAndUnknownPercent_AndMonthlyResetAreSupported()
    {
        var fallback = new ProviderUsageTierViewModel(new ProviderUsageTier(ProviderUsageTierId.Weekly,
            UsedPercentage: double.NaN, Remaining: 1, Total: 4));
        Assert.Equal(25d, fallback.RemainingPercentage);
        var unknown = new ProviderUsageTierViewModel(new ProviderUsageTier(ProviderUsageTierId.FiveHour));
        Assert.False(unknown.HasPercentage);
        Assert.Equal("--", unknown.PercentageText);
        var monthly = new ProviderUsageTierViewModel(new ProviderUsageTier(ProviderUsageTierId.Monthly,
            UsedPercentage: 100, ResetsAt: DateTimeOffset.UtcNow.AddMonths(1)));
        Assert.Equal("30-day usage", monthly.Label);
        Assert.True(monthly.HasReset);
        Assert.StartsWith("Resets ", monthly.ResetToolTip);
    }

    private static ProviderUsageSnapshot Quota(string id, params ProviderUsageTier[] tiers) =>
        new(id, ProviderUsageAdapterId.Kimi, ProviderUsageKind.CodingPlan, true, tiers, DateTimeOffset.UtcNow);

    private static ProviderUsageSnapshot Balance(string id, double remaining) =>
        new(id, ProviderUsageAdapterId.DeepSeek, ProviderUsageKind.Balance, true,
            [new ProviderUsageTier(ProviderUsageTierId.Balance, Remaining: remaining, Unit: "CNY")], DateTimeOffset.UtcNow);

    private sealed class ControlledService : IProviderUsageService
    {
        public List<PendingRequest> Requests { get; } = [];

        public Task<ProviderUsageSnapshot> GetUsageAsync(string providerId, string? baseUrl, string? apiKey,
            CancellationToken cancellationToken = default)
        {
            var request = new PendingRequest(cancellationToken);
            Requests.Add(request);
            return request.Completion.Task;
        }

        public void Complete(int index, ProviderUsageSnapshot snapshot) => Requests[index].Completion.SetResult(snapshot);
    }

    private sealed class PendingRequest(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<ProviderUsageSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
