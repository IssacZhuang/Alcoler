using System.Globalization;
using Alcoler.Workbench.Terminal;
using Alcoler.Workbench.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Alcoler.Workbench.Views;

/// <summary>
/// Custom-drawn terminal surface: one FormattedText run per same-color stretch of
/// cells. Redraws are driven exclusively by the view model's BufferChanged event.
/// </summary>
public sealed class TerminalSurface : Control
{
    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<TerminalSurface, double>(nameof(FontSize), 13);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<TerminalSurface, FontFamily>(
            nameof(FontFamily), FontFamily.Parse("Consolas,Courier New,monospace"));

    private readonly Dictionary<TerminalColor, ImmutableSolidColorBrush> _brushCache = [];

    static TerminalSurface() => FocusableProperty.OverrideDefaultValue<TerminalSurface>(true);

    public TerminalSurface()
    {
        ClipToBounds = true;
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public TerminalBuffer? Buffer { get; set; }

    public TerminalPaneViewModel? ViewModel { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Buffer is null)
        {
            return default;
        }
        GetMetrics(out var cellWidth, out var lineHeight);
        return new Size(Buffer.Cols * cellWidth, Buffer.Rows * lineHeight);
    }

    public override void Render(DrawingContext context)
    {
        if (Buffer is null)
        {
            return;
        }
        GetMetrics(out var cellWidth, out var lineHeight);
        var typeface = new Typeface(FontFamily);
        var defaultForeground = ResolveColor("AlcolerTextPrimary", Color.FromRgb(0xE6, 0xE7, 0xEA));

        for (var row = 0; row < Buffer.Rows; row++)
        {
            var cells = Buffer.GetRow(row);
            var runStart = 0;
            TerminalColor? runColor = cells[0].Foreground;
            for (var col = 1; col <= Buffer.Cols; col++)
            {
                TerminalColor? color = col < Buffer.Cols ? cells[col].Foreground : null;
                if (col < Buffer.Cols && cells[col].Foreground == runColor)
                {
                    continue;
                }
                if (runStart < col)
                {
                    var text = new string(cells[runStart..col].Select(c => c.Char).ToArray());
                    var foreground = runColor is { } rgb ? BrushFor(rgb) : new ImmutableSolidColorBrush(defaultForeground);
                    var formatted = new FormattedText(
                        text,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        FontSize,
                        foreground);
                    context.DrawText(formatted, new Point(runStart * cellWidth, row * lineHeight));
                }
                runStart = col;
                runColor = color;
            }
        }

        if (Buffer.CursorVisible)
        {
            var accent = ResolveColor("AlcolerAccent", Color.FromRgb(0x4C, 0x8D, 0xFF));
            var cursor = new Rect(Buffer.CursorCol * cellWidth, Buffer.CursorRow * lineHeight, cellWidth, lineHeight);
            context.FillRectangle(new ImmutableSolidColorBrush(new SolidColorBrush(accent, 0.4)), cursor);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (ViewModel is not { IsConnected: true } viewModel)
        {
            return;
        }
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (control && e.Key == Key.V)
        {
            PasteFromClipboard();
            e.Handled = true;
            return;
        }

        if (control && e.Key is >= Key.A and <= Key.Z)
        {
            // Control+letter maps to the C0 range (Ctrl+C -> ETX, Ctrl+D -> EOT, ...).
            viewModel.SendText(((char)(1 + (e.Key - Key.A))).ToString());
            e.Handled = true;
            return;
        }

        var key = e.Key switch
        {
            Key.Enter => TerminalKey.Enter,
            Key.Tab => TerminalKey.Tab,
            Key.Back => TerminalKey.Backspace,
            Key.Delete => TerminalKey.Delete,
            Key.Left => TerminalKey.Left,
            Key.Right => TerminalKey.Right,
            Key.Up => TerminalKey.Up,
            Key.Down => TerminalKey.Down,
            Key.Home => TerminalKey.Home,
            Key.End => TerminalKey.End,
            Key.PageUp => TerminalKey.PageUp,
            Key.PageDown => TerminalKey.PageDown,
            Key.Escape => TerminalKey.Escape,
            _ => (TerminalKey?)null,
        };
        if (key is { } mapped)
        {
            viewModel.HandleKeyInput(mapped, shift, control);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (ViewModel is { IsConnected: true } viewModel && !string.IsNullOrEmpty(e.Text))
        {
            viewModel.SendText(e.Text);
            e.Handled = true;
        }
    }

    private async void PasteFromClipboard()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }
        try
        {
            var text = await clipboard.GetTextAsync();
            if (!string.IsNullOrEmpty(text))
            {
                ViewModel?.Paste(text);
            }
        }
        catch (Exception)
        {
            // Clipboard access can fail transiently (e.g. locked by another process); paste is dropped.
        }
    }

    private ImmutableSolidColorBrush BrushFor(TerminalColor color)
    {
        if (!_brushCache.TryGetValue(color, out var brush))
        {
            brush = new ImmutableSolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            _brushCache[color] = brush;
        }
        return brush;
    }

    private Color ResolveColor(string resourceKey, Color fallback) =>
        this.TryFindResource(resourceKey, ActualThemeVariant, out var value) && value is ISolidColorBrush brush
            ? brush.Color
            : fallback;

    private void GetMetrics(out double cellWidth, out double lineHeight)
    {
        var prototype = new FormattedText(
            "M",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily),
            FontSize,
            Brushes.White);
        cellWidth = prototype.Width;
        lineHeight = prototype.Height;
        if (cellWidth <= 0 || lineHeight <= 0)
        {
            // Headless renderers may report empty bounds; fall back to nominal metrics.
            cellWidth = FontSize * 0.6;
            lineHeight = FontSize * 1.2;
        }
    }
}

public partial class TerminalView : UserControl
{
    private TerminalPaneViewModel? _viewModel;
    private bool _startAttempted;

    public TerminalView()
    {
        // The generated InitializeComponent(bool) overload wires x:Name fields; a manual parameterless overload would shadow it.
        InitializeComponent(true);
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.BufferChanged -= OnBufferChanged;
        }
        _viewModel = DataContext as TerminalPaneViewModel;
        if (_viewModel is not null)
        {
            Surface.ViewModel = _viewModel;
            Surface.Buffer = _viewModel.Buffer;
            _viewModel.BufferChanged += OnBufferChanged;
        }
        Surface.InvalidateVisual();
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.BufferChanged -= OnBufferChanged;
            _viewModel.BufferChanged += OnBufferChanged;
        }
        // Focus stays with the composer until the user clicks the terminal surface.
        if (_viewModel is { IsConnected: false } && !_startAttempted)
        {
            _startAttempted = true;
            _ = StartAsync(_viewModel);
        }
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.BufferChanged -= OnBufferChanged;
        }
    }

    private void OnBufferChanged(object? sender, EventArgs e) => Surface.InvalidateVisual();

    private async Task StartAsync(TerminalPaneViewModel viewModel)
    {
        try
        {
            await viewModel.StartAsync();
        }
        catch (Exception)
        {
            // Shell spawn failures surface as the offline status; nothing to reset.
        }
    }

    private void OnClearClick(object? sender, RoutedEventArgs e) => _viewModel?.Clear();

    private void OnStartClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is { } viewModel)
        {
            _ = StartAsync(viewModel);
        }
    }

    private void OnStopClick(object? sender, RoutedEventArgs e) => _ = _viewModel?.StopAsync();
}
