using System.Globalization;
using Alcoler.Terminal;
using Alcoler.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Alcoler.Views;

public sealed class TerminalSurface : Control
{
    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<TerminalSurface, double>(nameof(FontSize), 13);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<TerminalSurface, FontFamily>(
            nameof(FontFamily), FontFamily.Parse("Consolas,Courier New,monospace"));

    private readonly Dictionary<TerminalColor, ImmutableSolidColorBrush> _brushCache = [];
    private Typeface _typeface;
    private FontFamily? _typefaceFamily;
    private bool _metricsValid;
    private double _cellWidth;
    private double _lineHeight;
    private IBrush? _defaultForegroundBrush;
    private IBrush? _cursorBrush;
    private bool _themeBrushesValid;
    private char[] _runBuffer = [];
    private readonly Dictionary<int, RowRuns> _rowRuns = [];
    private TerminalBuffer? _rowRunsBuffer;
    private int _rowRunsRows = -1;

    static TerminalSurface() => FocusableProperty.OverrideDefaultValue<TerminalSurface>(true);

    public TerminalSurface()
    {
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) =>
        {
            _themeBrushesValid = false;
            InvalidateVisual();
        };
        ResourcesChanged += (_, _) =>
        {
            _themeBrushesValid = false;
            InvalidateVisual();
        };
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

    protected override Size MeasureOverride(Size availableSize) => default;

    protected override Size ArrangeOverride(Size finalSize)
    {
        ResizeToViewport(finalSize);
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontSizeProperty || change.Property == FontFamilyProperty)
        {
            _metricsValid = false;
            ResizeToViewport(Bounds.Size);
            InvalidateVisual();
        }
    }

    internal void ResizeToViewport(Size size)
    {
        if (ViewModel is null || !double.IsFinite(size.Width) || !double.IsFinite(size.Height)
            || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }
        GetMetrics(out var cellWidth, out var lineHeight);
        ViewModel.Resize(
            Math.Max(1, (int)Math.Floor(size.Width / cellWidth)),
            Math.Max(1, (int)Math.Floor(size.Height / lineHeight)));
    }

    public override void Render(DrawingContext context)
    {
        if (Buffer is null)
        {
            return;
        }
        var themeChanged = !_themeBrushesValid;
        var metricsChanged = !_metricsValid;
        GetMetrics(out var cellWidth, out var lineHeight);
        var typeface = CurrentTypeface();
        if (themeChanged)
        {
            var defaultForeground = ResolveColor("AlcolerTerminalForeground", Color.FromRgb(0xD4, 0xD4, 0xD4));
            var accent = ResolveColor("AlcolerAccent", Color.FromRgb(0x4C, 0x8D, 0xFF));
            _defaultForegroundBrush = new ImmutableSolidColorBrush(defaultForeground);
            _cursorBrush = new ImmutableSolidColorBrush(accent, 0.4);
            _themeBrushesValid = true;
        }
        var defaultBrush = _defaultForegroundBrush!;
        var cursorBrush = _cursorBrush!;
        var buffer = Buffer;

        // Cached runs embed the theme-dependent default brush and the metrics, so
        // any of those changing invalidates every row's cache entry.
        if (themeChanged || metricsChanged || !ReferenceEquals(_rowRunsBuffer, buffer) || _rowRunsRows != buffer.Rows)
        {
            _rowRuns.Clear();
            _rowRunsBuffer = buffer;
            _rowRunsRows = buffer.Rows;
        }

        for (var row = 0; row < buffer.Rows; row++)
        {
            if (!_rowRuns.TryGetValue(row, out var cache) || cache.Version != buffer.GetRowVersion(row))
            {
                cache = BuildRowRuns(row, buffer.GetRowVersion(row), buffer.GetRow(row), typeface, defaultBrush, cellWidth);
                _rowRuns[row] = cache;
            }
            var y = row * lineHeight;
            for (var run = 0; run < cache.Texts.Count; run++)
            {
                context.DrawText(cache.Texts[run], new Point(cache.Positions[run], y));
            }
        }

        if (buffer.CursorVisible)
        {
            var cursor = new Rect(buffer.CursorCol * cellWidth, buffer.CursorRow * lineHeight, cellWidth, lineHeight);
            context.FillRectangle(cursorBrush, cursor);
        }
    }

    private RowRuns BuildRowRuns(int row, int version, TerminalBuffer.Cell[] cells, Typeface typeface, IBrush defaultBrush, double cellWidth)
    {
        var cache = new RowRuns { Version = version };
        var runStart = 0;
        TerminalColor? runColor = cells[0].Foreground;
        for (var col = 1; col <= cells.Length; col++)
        {
            TerminalColor? color = col < cells.Length ? cells[col].Foreground : null;
            if (col < cells.Length && cells[col].Foreground == runColor)
            {
                continue;
            }
            if (runStart < col)
            {
                var length = col - runStart;
                if (_runBuffer.Length < length)
                    _runBuffer = new char[length];
                for (var index = 0; index < length; index++)
                    _runBuffer[index] = cells[runStart + index].Char;
                var text = new string(_runBuffer, 0, length);
                var foreground = runColor is { } rgb ? BrushFor(rgb) : defaultBrush;
                cache.Texts.Add(new FormattedText(
                    text,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    FontSize,
                    foreground));
                cache.Positions.Add(runStart * cellWidth);
            }
            runStart = col;
            runColor = color;
        }
        return cache;
    }

    private sealed class RowRuns
    {
        public int Version;
        public readonly List<FormattedText> Texts = [];
        public readonly List<double> Positions = [];
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

    private Typeface CurrentTypeface()
    {
        if (!Equals(_typefaceFamily, FontFamily))
        {
            _typeface = new Typeface(FontFamily);
            _typefaceFamily = FontFamily;
            _metricsValid = false;
        }
        return _typeface;
    }

    private void GetMetrics(out double cellWidth, out double lineHeight)
    {
        if (!_metricsValid)
        {
            var prototype = new FormattedText(
                "M",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                CurrentTypeface(),
                FontSize,
                Brushes.White);
            _cellWidth = prototype.Width;
            _lineHeight = prototype.Height;
            if (_cellWidth <= 0 || _lineHeight <= 0)
            {
                // Headless renderers may report empty bounds; fall back to nominal metrics.
                _cellWidth = FontSize * 0.6;
                _lineHeight = FontSize * 1.2;
            }
            _metricsValid = true;
        }
        cellWidth = _cellWidth;
        lineHeight = _lineHeight;
    }
}

public partial class TerminalView : UserControl
{
    private TerminalPaneViewModel? _viewModel;

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
        Surface.ViewModel = _viewModel;
        Surface.Buffer = _viewModel?.Buffer;
        if (_viewModel is not null)
        {
            _viewModel.BufferChanged += OnBufferChanged;
            Surface.ResizeToViewport(Surface.Bounds.Size);
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
