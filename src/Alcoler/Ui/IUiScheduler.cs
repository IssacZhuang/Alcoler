namespace Alcoler.Ui;

/// <summary>
/// Marshals continuations onto the UI thread. All ProjectionStore events and view-model
/// mutations run through this so handlers can assume single-threaded access.
/// </summary>
public interface IUiScheduler
{
    void Post(Action action);

    /// <summary>Posts work below input and rendering so streaming refreshes cannot starve interaction.</summary>
    void PostBackground(Action action) => Post(action);
}

public sealed class AvaloniaUiScheduler : IUiScheduler
{
    public static readonly AvaloniaUiScheduler Instance = new();

    public void Post(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action);

    public void PostBackground(Action action) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(action, Avalonia.Threading.DispatcherPriority.Background);
}

/// <summary>Test scheduler: runs actions inline on the calling thread.</summary>
public sealed class InlineUiScheduler : IUiScheduler
{
    public static readonly InlineUiScheduler Instance = new();

    public void Post(Action action) => action();
}
