using Alcoler.Adapters.Storage;
using Alcoler.Workbench.ViewModel;
using Alcoler.Workbench.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alcoler.Workbench.Controls;

public sealed class ConversationImage : Image
{
    public static readonly StyledProperty<string?> ArtifactUriProperty =
        AvaloniaProperty.Register<ConversationImage, string?>(nameof(ArtifactUri));

    public static readonly StyledProperty<string?> MimeTypeProperty =
        AvaloniaProperty.Register<ConversationImage, string?>(nameof(MimeType));

    public static readonly StyledProperty<string?> FileNameProperty =
        AvaloniaProperty.Register<ConversationImage, string?>(nameof(FileName));

    private readonly FileArtifactStore _artifacts;
    private CancellationTokenSource? _load;
    private Bitmap? _thumbnail;
    private Point? _pressedAt;
    private bool _opening;

    public ConversationImage() : this(new FileArtifactStore(WorkbenchDataPaths.ArtifactsPath))
    {
    }

    internal ConversationImage(FileArtifactStore artifacts)
    {
        _artifacts = artifacts;
        Width = 128;
        Height = 96;
        Stretch = Stretch.Uniform;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        AutomationProperties.SetName(this, "Preview image");
    }

    public string? ArtifactUri
    {
        get => GetValue(ArtifactUriProperty);
        set => SetValue(ArtifactUriProperty, value);
    }

    public string? MimeType
    {
        get => GetValue(MimeTypeProperty);
        set => SetValue(MimeTypeProperty, value);
    }

    public string? FileName
    {
        get => GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressedAt = e.GetPosition(this);
            Focus();
            e.Handled = true;
        }
    }

    protected override async void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var pressedAt = _pressedAt;
        _pressedAt = null;
        if (e.InitialPressMouseButton == MouseButton.Left && pressedAt is { } start
            && new Rect(Bounds.Size).Contains(e.GetPosition(this))
            && Math.Abs(e.GetPosition(this).X - start.X) <= 4
            && Math.Abs(e.GetPosition(this).Y - start.Y) <= 4)
        {
            e.Handled = true;
            await OpenPreviewAsync();
        }
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            await OpenPreviewAsync();
        }
    }

    private async Task OpenPreviewAsync()
    {
        if (_opening || ArtifactUri is not { } uri || MimeType is not { } mime
            || TopLevel.GetTopLevel(this) is not Window owner)
            return;
        _opening = true;
        try
        {
            await ImageViewerDialog.ShowArtifactAsync(owner, uri, mime, FileName, _artifacts);
        }
        finally
        {
            _opening = false;
        }
    }

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LoadThumbnail();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ReleaseThumbnail();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FileNameProperty)
            AutomationProperties.SetName(this, string.IsNullOrWhiteSpace(FileName) ? "Preview image" : $"Preview image: {FileName}");
        if ((change.Property == ArtifactUriProperty || change.Property == MimeTypeProperty) && this.IsAttachedToVisualTree())
            LoadThumbnail();
    }

    private void ReleaseThumbnail()
    {
        _load?.Cancel();
        _load?.Dispose();
        _load = null;
        Source = null;
        _thumbnail?.Dispose();
        _thumbnail = null;
    }

    private async void LoadThumbnail()
    {
        ReleaseThumbnail();
        if (ArtifactUri is not { } uri || MimeType is not { } mime)
            return;
        var loading = _load = new CancellationTokenSource();
        var token = loading.Token;
        try
        {
            var thumbnail = await Task.Run(async () =>
            {
                var data = await _artifacts.TryLoadAsync(uri, token);
                if (data is null || token.IsCancellationRequested)
                    return null;
                return ComposerImageAttachment.DecodeThumbnail(data.Value.ToArray(), mime.ToLowerInvariant(), 192);
            }, token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_load != loading || token.IsCancellationRequested)
                    thumbnail?.Dispose();
                else
                    Source = _thumbnail = thumbnail;
            });
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException
            or ArgumentException or NotSupportedException)
        {
        }
    }
}
