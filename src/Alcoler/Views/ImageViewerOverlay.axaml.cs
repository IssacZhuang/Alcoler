using Alcoler.Adapters.Storage;
using Alcoler.Localization;
using Alcoler.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alcoler.Views;

public partial class ImageViewerOverlay : UserControl
{
    private static readonly AttachedProperty<ImageViewerOverlay?> OpenViewerProperty =
        AvaloniaProperty.RegisterAttached<ImageViewerOverlay, Window, ImageViewerOverlay?>("OpenViewer");

    private readonly CancellationTokenSource _loading = new();
    private readonly TaskCompletionSource<bool> _closedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<CancellationToken, Task<Bitmap?>>? _loader;
    private Bitmap? _bitmap;
    private Window? _owner;
    private AdornerLayer? _layer;
    private IInputElement? _previousFocus;
    private bool _closed;
    private bool _disposed;
    private bool _loadingStarted;
    private bool _closeOnLoadError;
    private bool _loadFailed;
    private bool _fit = true;
    private double _zoom = 1;

    public ImageViewerOverlay()
    {
        InitializeComponent(true);
        AddHandler(KeyDownEvent, OnViewerKeyDown, RoutingStrategies.Tunnel);
        ImageScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty && _fit)
                UpdateImageSize();
        };
    }

    internal ImageViewerOverlay(string? fileName, Func<CancellationToken, Task<Bitmap?>> loader) : this()
    {
        FileNameLabel.Text = string.IsNullOrWhiteSpace(fileName) ? Loc.Get("imageviewer_title") : fileName;
        _loader = loader;
    }

    internal static Task ShowAsync(Window owner, ComposerImageAttachment attachment)
    {
        try
        {
            var bytes = attachment.ReadPreviewBytes();
            var mimeType = attachment.MimeType;
            return ShowCore(owner, new ImageViewerOverlay(attachment.Name, token => Task.Run<Bitmap?>(() =>
                ComposerImageAttachment.DecodeThumbnail(bytes, mimeType, 32_768), token)));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException or UnauthorizedAccessException)
        {
            return ShowCore(owner, new ImageViewerOverlay(attachment.Name, _ => Task.FromException<Bitmap?>(exception)));
        }
    }

    internal static async Task<bool> TryShowFileAsync(Window owner, string path, string mimeType)
    {
        var overlay = new ImageViewerOverlay(Path.GetFileName(path), token => Task.Run<Bitmap?>(async () =>
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = stream.Length;
            ComposerImageAttachment.ValidateSize(length);
            var bytes = new byte[(int)length];
            await stream.ReadExactlyAsync(bytes, token);
            token.ThrowIfCancellationRequested();
            return ComposerImageAttachment.DecodeThumbnail(bytes, mimeType.ToLowerInvariant(), 32_768);
        }, token))
        {
            _closeOnLoadError = true,
        };
        await ShowCore(owner, overlay);
        return !overlay._loadFailed;
    }

    internal static Task ShowArtifactAsync(Window owner, string uri, string mimeType, string? fileName,
        FileArtifactStore? store = null) =>
        ShowCore(owner, new ImageViewerOverlay(fileName, token => Task.Run(async () =>
        {
            var data = await (store ?? new FileArtifactStore(WorkbenchDataPaths.ArtifactsPath)).TryLoadAsync(uri, token);
            token.ThrowIfCancellationRequested();
            return data is null ? null : ComposerImageAttachment.DecodeThumbnail(data.Value.ToArray(), mimeType.ToLowerInvariant(), 32_768);
        }, token)));

    internal static Task ShowAsync(Window owner, string? fileName, Func<CancellationToken, Task<Bitmap?>> loader) =>
        ShowCore(owner, new ImageViewerOverlay(fileName, loader));

    internal static bool IsOpen(Window owner) => owner.GetValue(OpenViewerProperty) is { _closed: false };

    internal void Close()
    {
        if (_closed)
            return;
        _closed = true;
        var layer = _layer;
        _layer = null;
        layer?.Children.Remove(this);
        Cleanup();
        _closedSource.TrySetResult(true);
    }

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_loadingStarted)
            return;
        _loadingStarted = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed)
                CloseImageButton.Focus();
        }, DispatcherPriority.Input);
        var loader = _loader;
        _loader = null;
        try
        {
            var bitmap = loader is null ? null : await loader(_loading.Token);
            if (_closed)
            {
                bitmap?.Dispose();
                return;
            }
            if (bitmap is null)
            {
                ShowError(Loc.Get("imageviewer_unavailable"));
                return;
            }
            PreviewImage.Source = _bitmap = bitmap;
            StatusLabel.IsVisible = false;
            ZoomControls.IsEnabled = true;
            UpdateImageSize();
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or ObjectDisposedException)
        {
            if (!_closed)
                ShowError(Loc.Get("imageviewer_open_failed"));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (!_closed)
        {
            _closed = true;
            _closedSource.TrySetResult(true);
        }
        Cleanup();
        base.OnDetachedFromVisualTree(e);
    }

    private static Task ShowCore(Window owner, ImageViewerOverlay overlay)
    {
        if (!overlay.Attach(owner))
        {
            overlay._loadFailed = true;
            overlay.Close();
            return Task.CompletedTask;
        }
        return overlay._closedSource.Task;
    }

    private bool Attach(Window owner)
    {
        if (!owner.IsVisible)
            return false;
        var layer = owner.Content is Visual anchor && anchor.IsAttachedToVisualTree()
            ? AdornerLayer.GetAdornerLayer(anchor)
            : null;
        if (layer is null)
        {
            foreach (var visual in owner.GetVisualDescendants().OfType<Visual>().ToArray())
            {
                layer = AdornerLayer.GetAdornerLayer(visual);
                if (layer is not null)
                    break;
            }
        }
        if (layer is null)
            return false;
        foreach (var existing in layer.Children.OfType<ImageViewerOverlay>().ToArray())
            existing.Close();
        _owner = owner;
        _layer = layer;
        _previousFocus = owner.FocusManager?.GetFocusedElement();
        Width = owner.ClientSize.Width;
        Height = owner.ClientSize.Height;
        owner.SizeChanged += OnOwnerSizeChanged;
        owner.SetValue(OpenViewerProperty, this);
        layer.Children.Add(this);
        return true;
    }

    private void OnOwnerSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_owner is not { } owner)
            return;
        Width = owner.ClientSize.Width;
        Height = owner.ClientSize.Height;
    }

    private void Cleanup()
    {
        if (_disposed)
            return;
        _disposed = true;
        _loading.Cancel();
        _loading.Dispose();
        PreviewImage.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        if (_owner is { } owner)
        {
            owner.SizeChanged -= OnOwnerSizeChanged;
            if (ReferenceEquals(owner.GetValue(OpenViewerProperty), this))
                owner.ClearValue(OpenViewerProperty);
        }
        _owner = null;
        var previous = _previousFocus;
        _previousFocus = null;
        if (previous is InputElement { IsEffectivelyEnabled: true } element && element.IsAttachedToVisualTree())
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (element.IsAttachedToVisualTree())
                    element.Focus();
            }, DispatcherPriority.Input);
        }
    }

    private void ShowError(string message)
    {
        _loadFailed = true;
        if (_closeOnLoadError)
        {
            Close();
            return;
        }
        StatusLabel.Text = message;
        StatusLabel.IsVisible = true;
    }

    private void UpdateImageSize()
    {
        if (_bitmap is not { } bitmap || _closed)
            return;
        if (_fit)
        {
            var viewport = ImageScroll.Viewport;
            if (viewport.Width <= 0 || viewport.Height <= 0)
                return;
            _zoom = Math.Min(1, Math.Min(viewport.Width / bitmap.PixelSize.Width, viewport.Height / bitmap.PixelSize.Height));
            ImageScroll.Offset = default;
        }
        PreviewImage.Width = bitmap.PixelSize.Width * _zoom;
        PreviewImage.Height = bitmap.PixelSize.Height * _zoom;
        ZoomLabel.Text = $"{_zoom:P0}";
        ZoomOutButton.IsEnabled = _zoom > .01;
        ZoomInButton.IsEnabled = _zoom < 8;
    }

    private void Zoom(double factor)
    {
        _fit = false;
        _zoom = Math.Clamp(_zoom * factor, .01, 8);
        UpdateImageSize();
    }

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => Zoom(1 / 1.25);
    private void OnZoomInClick(object? sender, RoutedEventArgs e) => Zoom(1.25);

    private void OnActualSizeClick(object? sender, RoutedEventArgs e)
    {
        _fit = false;
        _zoom = 1;
        UpdateImageSize();
    }

    private void OnFitClick(object? sender, RoutedEventArgs e)
    {
        _fit = true;
        UpdateImageSize();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_closed || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (e.Source is Visual source)
        {
            if (source.GetSelfAndVisualAncestors().Any(visual =>
                    visual == Toolbar || visual is Button or ScrollBar or Track or Thumb))
                return;
        }
        if (PreviewImage.Source is not null && new Rect(PreviewImage.Bounds.Size).Contains(e.GetPosition(PreviewImage)))
            return;
        e.Handled = true;
        Close();
    }

    private void OnViewerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _closed)
            return;
        e.Handled = true;
        Close();
    }
}
