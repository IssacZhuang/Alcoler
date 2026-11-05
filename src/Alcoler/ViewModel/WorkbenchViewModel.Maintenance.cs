using Alcoler.Audio;
using Alcoler.Contracts.Identifiers;

namespace Alcoler.ViewModel;

public sealed partial class WorkbenchViewModel
{
    private async Task TryAutoArchiveAsync()
    {
        if (!_config.AutoArchiveSessions || _autoArchiveRunning || _disposed)
        {
            return;
        }
        var now = DateTimeOffset.UtcNow;
        if (_lastAutoArchiveAt is { } last && now - last < AutoArchiveInterval)
        {
            return;
        }

        _autoArchiveRunning = true;
        try
        {
            var days = Math.Clamp(_config.AutoArchiveOlderThanDays, 1, 365);
            await _runtime.ArchiveStaleSessionsAsync(
                TimeSpan.FromDays(days), Active?.SessionId, CancellationToken.None);
            _lastAutoArchiveAt = now;
        }
        catch (Exception)
        {
            // Automatic cleanup must not prevent the session list from loading.
        }
        finally
        {
            _autoArchiveRunning = false;
        }
    }

    // RefreshFromRuntimeAsync only runs on user-driven refreshes; idle unloading must still
    // progress while the app sits unused, so the tick loop drives it on its own timer.
    private async Task RunMaintenanceTicksAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MaintenanceTickInterval);
        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_disposed)
            {
                return;
            }
            try
            {
                await TryAutoArchiveAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            try
            {
                await _runtime.UnloadIdleSessionsAsync(IdleSessionUnloadThreshold, Active?.SessionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A failed maintenance action must not kill the loop.
            }
        }
    }

    private void OnSessionActivityChanged(SessionId sessionId)
    {
        if (_disposed)
        {
            return;
        }
        // Alert edge detection needs the snapshot as of the event; a posted read can land
        // after further transitions already completed and would merge them away.
        var activityAtEvent = _activities?.GetSessionActivity(sessionId);
        _scheduler.Post(() =>
        {
            if (_disposed)
            {
                return;
            }
            var activity = _activities?.GetSessionActivity(sessionId);
            RefreshSessionActivities();
            if (activity is not null && string.IsNullOrEmpty(activity.SessionId.Value))
                SessionActivities.ApplyActivity(sessionId, activity);
            if (_busyPeriods.OnActivity(sessionId, activity?.Phase))
            {
                _ = Task.Run(GcMaintenance.CompactLargeObjectHeap);
            }
            if (_taskAlerts.OnActivity(sessionId, activityAtEvent) is { } alert
                && (alert is TaskAlert.TaskCompleted
                    ? _config.Alerts?.TaskCompleted ?? true
                    : _config.Alerts?.AwaitingConfirmation ?? true))
            {
                _taskAlertSounds.Play(alert);
            }
        });
    }
}
