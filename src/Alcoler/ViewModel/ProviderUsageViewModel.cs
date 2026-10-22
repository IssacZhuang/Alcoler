using System.Globalization;
using Alcoler.Adapters.ProviderUsage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Alcoler.ViewModel;

public sealed class ProviderUsageViewModel : ObservableObject, IDisposable
{
    private readonly IProviderUsageService _service;
    private readonly object _gate = new();
    private string _providerId = string.Empty;
    private string _baseUrl = string.Empty;
    private string _apiKey = string.Empty;
    private long _configurationVersion;
    private Request? _activeRequest;
    private bool _disposed;
    private ProviderUsageSnapshot? _snapshot;
    private IReadOnlyList<ProviderUsageTierViewModel> _quotaTiers = [];
    private ProviderUsageSnapshot? _notifiedSnapshot;
    private IReadOnlyList<ProviderUsageTierViewModel> _notifiedQuotaTiers = [];
    private bool _notifiedIsSupported;
    private bool _notifiedIsVisible;
    private bool _notifiedIsLoading;
    private bool _notifiedCanRefresh;
    private bool _notifiedHasError;
    private bool _notifiedHasData;
    private bool _notifiedIsEmpty;
    private bool _notifiedIsBalance;
    private bool _notifiedShowBalance;
    private bool _notifiedShowQuota;
    private int _notifiedQuotaColumns;
    private string? _notifiedBalanceText;
    private string? _notifiedBalanceDetail;
    private bool _notifiedHasBalanceDetail;
    private string? _notifiedErrorMessage;

    public ProviderUsageViewModel(IProviderUsageService? service = null)
    {
        _service = service ?? new ProviderUsageService();
        _notifiedQuotaTiers = _quotaTiers;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => CanRefresh);
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public ProviderUsageSnapshot? Snapshot => _snapshot;
    public bool IsSupported => ProviderUsageDetection.DetectAdapter(_baseUrl) is not null;
    public bool IsVisible => !_disposed && IsSupported
        && !string.IsNullOrWhiteSpace(_providerId) && !string.IsNullOrWhiteSpace(_apiKey);
    public bool IsLoading => _activeRequest is not null;
    public bool CanRefresh => IsVisible && !IsLoading;
    public bool HasError => _snapshot?.Error is not null;
    public bool HasData => _snapshot is { Error: null } && _snapshot.Tiers.Count > 0;
    public bool IsEmpty => _snapshot is { Error: null, Tiers.Count: 0 } && !IsLoading;
    public bool IsBalance => _snapshot?.Kind == ProviderUsageKind.Balance;
    public bool ShowBalance => HasData && IsBalance;
    public bool ShowQuota => HasData && !IsBalance;
    public IReadOnlyList<ProviderUsageTierViewModel> QuotaTiers => _quotaTiers;
    public int QuotaColumns => Math.Clamp(_quotaTiers.Count, 1, 3);
    public string BalanceText => _snapshot?.Tiers.FirstOrDefault(tier => tier.Id == ProviderUsageTierId.Balance)
        is { Remaining: { } remaining } tier && double.IsFinite(remaining)
            ? $"{remaining.ToString("N2", CultureInfo.CurrentCulture)} {tier.Unit}".TrimEnd()
            : "--";
    public string BalanceDetail => _snapshot?.Tiers.FirstOrDefault(tier => tier.Id == ProviderUsageTierId.Balance)
        is { Used: { } used, Total: { } total } tier
            ? $"{used.ToString("N2", CultureInfo.CurrentCulture)} used / {total.ToString("N2", CultureInfo.CurrentCulture)} {tier.Unit}".TrimEnd()
            : string.Empty;
    public bool HasBalanceDetail => BalanceDetail.Length > 0;
    public string ErrorMessage => _snapshot?.Error?.Code switch
    {
        ProviderUsageErrorCode.AuthFailed => "Authentication failed. Check the API key and retry.",
        ProviderUsageErrorCode.Network => "Could not reach the usage service. Try again.",
        ProviderUsageErrorCode.BadResponse => "The provider returned an invalid usage response.",
        _ => "Could not load usage. Try again.",
    };

    // Configuration edits invalidate results but never trigger credential-bearing requests.
    public void Configure(string? providerId, string? baseUrl, string? apiKey)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            providerId ??= string.Empty;
            baseUrl ??= string.Empty;
            apiKey ??= string.Empty;
            if (_providerId == providerId && _baseUrl == baseUrl && _apiKey == apiKey)
            {
                return;
            }
            _providerId = providerId;
            _baseUrl = baseUrl;
            _apiKey = apiKey;
            _configurationVersion++;
            CancelActiveRequest();
            _snapshot = null;
            _quotaTiers = [];
            NotifyStateChanged();
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_disposed || !IsVisible)
            {
                return Task.CompletedTask;
            }
            if (_activeRequest is { } active)
            {
                return active.Completion.Task;
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }
            var request = new Request(_configurationVersion,
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            _activeRequest = request;
            NotifyStateChanged();
            _ = FetchAsync(request, _providerId, _baseUrl, _apiKey);
            return request.Completion.Task;
        }
    }

    private async Task FetchAsync(Request request, string providerId, string baseUrl, string apiKey)
    {
        try
        {
            var snapshot = await _service.GetUsageAsync(providerId, baseUrl, apiKey, request.Cancellation.Token);
            lock (_gate)
            {
                if (IsCurrent(request) && !request.Cancellation.IsCancellationRequested)
                {
                    ApplySnapshot(snapshot);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            lock (_gate)
            {
                if (IsCurrent(request))
                {
                    var adapter = ProviderUsageDetection.DetectAdapter(baseUrl);
                    ApplySnapshot(new ProviderUsageSnapshot(providerId, adapter,
                        adapter is { } id ? ProviderUsageDetection.GetKind(id) : null,
                        true, [], DateTimeOffset.UtcNow,
                        new ProviderUsageError(ProviderUsageErrorCode.Unknown, "Could not load usage.")));
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (IsCurrent(request))
                {
                    _activeRequest = null;
                    NotifyStateChanged();
                }
            }
            request.Cancellation.Dispose();
            request.Completion.TrySetResult();
        }
    }

    private bool IsCurrent(Request request) => !_disposed
        && ReferenceEquals(request, _activeRequest) && request.Version == _configurationVersion;

    private void ApplySnapshot(ProviderUsageSnapshot snapshot)
    {
        _snapshot = snapshot;
        _quotaTiers = snapshot.Error is null
            ? snapshot.Tiers.Where(tier => tier.Id != ProviderUsageTierId.Balance)
                .Select(tier => new ProviderUsageTierViewModel(tier)).ToArray()
            : [];
        NotifyStateChanged();
    }

    private void CancelActiveRequest()
    {
        var previous = _activeRequest;
        _activeRequest = null;
        previous?.Cancellation.Cancel();
    }

    private void NotifyStateChanged()
    {
        if (!ReferenceEquals(_notifiedSnapshot, _snapshot))
        {
            _notifiedSnapshot = _snapshot;
            OnPropertyChanged(nameof(Snapshot));
        }
        if (_notifiedIsSupported != IsSupported)
        {
            _notifiedIsSupported = IsSupported;
            OnPropertyChanged(nameof(IsSupported));
        }
        if (_notifiedIsVisible != IsVisible)
        {
            _notifiedIsVisible = IsVisible;
            OnPropertyChanged(nameof(IsVisible));
        }
        if (_notifiedIsLoading != IsLoading)
        {
            _notifiedIsLoading = IsLoading;
            OnPropertyChanged(nameof(IsLoading));
        }
        if (_notifiedCanRefresh != CanRefresh)
        {
            _notifiedCanRefresh = CanRefresh;
            OnPropertyChanged(nameof(CanRefresh));
        }
        if (_notifiedHasError != HasError)
        {
            _notifiedHasError = HasError;
            OnPropertyChanged(nameof(HasError));
        }
        if (_notifiedHasData != HasData)
        {
            _notifiedHasData = HasData;
            OnPropertyChanged(nameof(HasData));
        }
        if (_notifiedIsEmpty != IsEmpty)
        {
            _notifiedIsEmpty = IsEmpty;
            OnPropertyChanged(nameof(IsEmpty));
        }
        if (_notifiedIsBalance != IsBalance)
        {
            _notifiedIsBalance = IsBalance;
            OnPropertyChanged(nameof(IsBalance));
        }
        if (_notifiedShowBalance != ShowBalance)
        {
            _notifiedShowBalance = ShowBalance;
            OnPropertyChanged(nameof(ShowBalance));
        }
        if (_notifiedShowQuota != ShowQuota)
        {
            _notifiedShowQuota = ShowQuota;
            OnPropertyChanged(nameof(ShowQuota));
        }
        if (!ReferenceEquals(_notifiedQuotaTiers, _quotaTiers))
        {
            _notifiedQuotaTiers = _quotaTiers;
            OnPropertyChanged(nameof(QuotaTiers));
        }
        var quotaColumns = QuotaColumns;
        if (_notifiedQuotaColumns != quotaColumns)
        {
            _notifiedQuotaColumns = quotaColumns;
            OnPropertyChanged(nameof(QuotaColumns));
        }
        var balanceText = BalanceText;
        if (!string.Equals(_notifiedBalanceText, balanceText, StringComparison.Ordinal))
        {
            _notifiedBalanceText = balanceText;
            OnPropertyChanged(nameof(BalanceText));
        }
        var balanceDetail = BalanceDetail;
        if (!string.Equals(_notifiedBalanceDetail, balanceDetail, StringComparison.Ordinal))
        {
            _notifiedBalanceDetail = balanceDetail;
            OnPropertyChanged(nameof(BalanceDetail));
        }
        var hasBalanceDetail = balanceDetail.Length > 0;
        if (_notifiedHasBalanceDetail != hasBalanceDetail)
        {
            _notifiedHasBalanceDetail = hasBalanceDetail;
            OnPropertyChanged(nameof(HasBalanceDetail));
        }
        var errorMessage = ErrorMessage;
        if (!string.Equals(_notifiedErrorMessage, errorMessage, StringComparison.Ordinal))
        {
            _notifiedErrorMessage = errorMessage;
            OnPropertyChanged(nameof(ErrorMessage));
        }
        RefreshCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _configurationVersion++;
            CancelActiveRequest();
            _apiKey = string.Empty;
            _snapshot = null;
            _quotaTiers = [];
            NotifyStateChanged();
        }
    }

    private sealed class Request(long version, CancellationTokenSource cancellation)
    {
        public long Version { get; } = version;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class ProviderUsageTierViewModel
{
    public ProviderUsageTierViewModel(ProviderUsageTier tier)
    {
        Tier = tier;
        RemainingPercentage = tier.UsedPercentage is { } used && double.IsFinite(used)
            ? Math.Clamp(100 - used, 0, 100)
            : tier is { Remaining: { } remaining, Total: { } total } && total > 0
                && double.IsFinite(remaining) && double.IsFinite(total)
                ? Math.Clamp(remaining / total * 100, 0, 100)
                : null;
        var reset = tier.ResetsAt?.ToLocalTime();
        ResetText = reset is { } date
            ? date.ToString(tier.Id == ProviderUsageTierId.FiveHour && date.Date == DateTime.Today
                ? "HH:mm" : "M/d", CultureInfo.CurrentCulture)
            : string.Empty;
    }

    public ProviderUsageTier Tier { get; }
    public double? RemainingPercentage { get; }
    public double MeterValue => RemainingPercentage ?? 0;
    public bool HasPercentage => RemainingPercentage is not null;
    public string PercentageText => RemainingPercentage is { } remaining
        ? remaining.ToString(remaining >= 10 ? "0" : "0.#", CultureInfo.CurrentCulture) + "%"
        : "--";
    public string Label => Tier.Id switch
    {
        ProviderUsageTierId.FiveHour => "5-hour usage",
        ProviderUsageTierId.Weekly => "7-day usage",
        ProviderUsageTierId.Monthly => "30-day usage",
        _ => "Balance",
    };
    public string ResetText { get; }
    public bool HasReset => ResetText.Length > 0;
    public string ResetToolTip => Tier.ResetsAt is { } date
        ? "Resets " + date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : string.Empty;
}
