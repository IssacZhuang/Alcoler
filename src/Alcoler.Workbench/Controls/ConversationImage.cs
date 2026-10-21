using Alcoler.Adapters.Storage;
using Alcoler.Workbench.ViewModel;
using Avalonia;
using Avalonia.Controls;
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

    private CancellationTokenSource? _load;
    private Bitmap? _thumbnail;

    public ConversationImage()
    {
        Width = 128;
        Height = 96;
        Stretch = Stretch.Uniform;
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
                var data = await new FileArtifactStore(WorkbenchDataPaths.ArtifactsPath).TryLoadAsync(uri, token);
                if (data is null || token.IsCancellationRequested)
                    return null;
                return ComposerImageAttachment.DecodeThumbnail(data.Value.ToArray(), mime, 192);
            }, token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_load != loading || token.IsCancellationRequested)
                    thumbnail?.Dispose();
                else
                    Source = _thumbnail = thumbnail;
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException
            or ArgumentException or NotSupportedException)
        {
        }
    }
}
