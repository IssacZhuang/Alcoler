using Alcoler.Contracts.Identifiers;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alcoler.Views;

public partial class TaskOverlayWindow : Window
{
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _isShuttingDown;
    private bool _positioned;

    public TaskOverlayWindow()
    {
        InitializeComponent();
        _refreshTimer.Tick += (_, _) => (DataContext as TaskOverlayViewModel)?.Refresh();
        PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty)
            {
                if (IsVisible)
                {
                    (DataContext as TaskOverlayViewModel)?.Refresh();
                    _refreshTimer.Start();
                }
                else
                {
                    _refreshTimer.Stop();
                }
            }
        };
        Closed += (_, _) => _refreshTimer.Stop();
        WindowChrome.Attach(this);
    }

    public TaskOverlayWindow(TaskOverlayViewModel viewModel) : this() => DataContext = viewModel;

    public event Action? MainWindowRequested;
    public event Action<SessionId>? SessionRequested;

    public void ShowNear(Window anchor)
    {
        var bounds = new PixelRect(Position, new PixelSize((int)(Width * RenderScaling), (int)(Bounds.Height * RenderScaling)));
        var screen = _positioned ? Screens.All.FirstOrDefault(screen => screen.WorkingArea.Intersects(bounds)) : null;
        var reposition = screen is null;
        screen ??= anchor.Screens.ScreenFromWindow(anchor) ?? anchor.Screens.Primary;
        if (screen is not null)
        {
            Width = Math.Max(1, Math.Min(344, screen.WorkingArea.Width / screen.Scaling - 40));
            MaxHeight = Math.Max(1, Math.Min(520, screen.WorkingArea.Height / screen.Scaling - 40));
            var margin = (int)(20 * screen.Scaling);
            var width = (int)(Width * screen.Scaling);
            var height = (int)(Math.Min(MaxHeight, Bounds.Height > 0 ? Bounds.Height : MaxHeight) * screen.Scaling);
            Position = reposition
                ? new PixelPoint(Math.Max(screen.WorkingArea.X, screen.WorkingArea.Right - width - margin), screen.WorkingArea.Y + margin)
                : new PixelPoint(Math.Clamp(Position.X, screen.WorkingArea.X, Math.Max(screen.WorkingArea.X, screen.WorkingArea.Right - width)),
                    Math.Clamp(Position.Y, screen.WorkingArea.Y, Math.Max(screen.WorkingArea.Y, screen.WorkingArea.Bottom - height)));
            _positioned = true;
        }
        Show();
    }

    internal void Shutdown()
    {
        _isShuttingDown = true;
        _refreshTimer.Stop();
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!_isShuttingDown)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || (e.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<Button>().Any()))
        {
            return;
        }
        if (e.ClickCount == 2)
            MainWindowRequested?.Invoke();
        else
            BeginMoveDrag(e);
        e.Handled = true;
    }

    private void OnHeaderKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source == OverlayHeader && e.Key is Key.Enter or Key.Space)
        {
            MainWindowRequested?.Invoke();
            e.Handled = true;
        }
    }

    private void OnHideClick(object? sender, RoutedEventArgs e) => Hide();

    private void OnSessionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TaskOverlaySessionViewModel session })
            SessionRequested?.Invoke(session.SessionId);
    }
}
