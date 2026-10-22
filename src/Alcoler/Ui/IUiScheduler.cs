namespace Alcoler.Ui;

/// <summary>
/// Marshals continuations onto the UI thread. All ProjectionStore events and view-model
/// mutations run through this so handlers can assume single-threaded access.
/// </summary>
public interface IUiScheduler
{
    void Post(Action action);
}

public sealed class AvaloniaUiScheduler : IUiScheduler
{
    public static readonly AvaloniaUiScheduler Instance = new();

    public void Post(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action);
}

/// <summary>Test scheduler: runs actions inline on the calling thread.</summary>
public sealed class InlineUiScheduler : IUiScheduler
{
    public static readonly InlineUiScheduler Instance = new();

    public void Post(Action action) => action();
}
