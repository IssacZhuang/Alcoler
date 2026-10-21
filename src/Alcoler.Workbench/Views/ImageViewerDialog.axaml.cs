using Alcoler.Adapters.Storage;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

namespace Alcoler.Workbench.Views;

public partial class ImageViewerDialog : Window
{
    private Func<CancellationToken, Task<Bitmap?>>? _loader;
    private readonly CancellationTokenSource _loading = new();
    private Bitmap? _bitmap;
    private bool _closed;
    private bool _fit = true;
    private double _zoom = 1;

    public ImageViewerDialog()
    {
        InitializeComponent(true);
        WindowChrome.Attach(this);
        AddHandler(KeyDownEvent, OnViewerKeyDown, RoutingStrategies.Tunnel);
        ImageScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty && _fit)
                UpdateImageSize();
        };
    }

    internal ImageViewerDialog(string? fileName, Func<CancellationToken, Task<Bitmap?>> loader) : this()
    {
        FileNameLabel.Text = string.IsNullOrWhiteSpace(fileName) ? "Image preview" : fileName;
        Title = FileNameLabel.Text;
        _loader = loader;
    }

    internal static Task ShowAsync(Window owner, ComposerImageAttachment attachment)
    {
        try
        {
            var bytes = attachment.ReadPreviewBytes();
            var mimeType = attachment.MimeType;
            return new ImageViewerDialog(attachment.Name, token => Task.Run<Bitmap?>(() =>
                ComposerImageAttachment.DecodeThumbnail(bytes, mimeType, 32_768), token)).ShowDialog(owner);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException or UnauthorizedAccessException)
        {
            return new ImageViewerDialog(attachment.Name, _ => Task.FromException<Bitmap?>(exception)).ShowDialog(owner);
        }
    }

    internal static Task ShowArtifactAsync(Window owner, string uri, string mimeType, string? fileName,
        FileArtifactStore? store = null) =>
        new ImageViewerDialog(fileName, token => Task.Run(async () =>
        {
            var data = await (store ?? new FileArtifactStore(WorkbenchDataPaths.ArtifactsPath)).TryLoadAsync(uri, token);
            token.ThrowIfCancellationRequested();
            return data is null ? null : ComposerImageAttachment.DecodeThumbnail(data.Value.ToArray(), mimeType.ToLowerInvariant(), 32_768);
        }, token)).ShowDialog(owner);

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        CloseImageButton.Focus();
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
                ShowError("The image is no longer available.");
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
                ShowError("Could not open this image.");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _loader = null;
        _loading.Cancel();
        _loading.Dispose();
        PreviewImage.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        base.OnClosed(e);
    }

    private void ShowError(string message)
    {
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

    private void OnViewerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        Close();
    }
}
