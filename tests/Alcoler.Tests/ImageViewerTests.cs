using System.Buffers.Binary;
using System.Text.Json;
using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Controls;
using Alcoler.Stores;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Alcoler.Views;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Alcoler.Tests;

public sealed class ImageViewerTests
{
    [AvaloniaFact]
    public async Task Composer_ClickPreviewRetainsAttachmentAndRemoveDoesNotOpenViewer()
    {
        ComposerImageAttachment attachment;
        using (UseImageDecoder())
            attachment = ComposerImageAttachment.FromBytes(CreatePng(1600, 800), "image/png", "screenshot.png");
        var host = new DraftConversationViewModel(Environment.CurrentDirectory, null, _ => Task.CompletedTask);
        host.Composer.AddAttachments([attachment]);
        var view = new ComposerView { DataContext = host };
        var owner = new Window { Width = 900, Height = 600, Content = view };
        owner.Show();
        try
        {
            var preview = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == "Preview image");
            await RenderAsync(owner);
            ImageViewerOverlay overlay;
            using (UseImageDecoder())
            {
                Click(owner, preview);
                overlay = FindOverlay(owner);
                await WaitForAsync(() => overlay.FindControl<Image>("PreviewImage")!.Source is not null);
            }
            Assert.Equal("screenshot.png", overlay.FindControl<TextBlock>("FileNameLabel")!.Text);
            Assert.Equal(new PixelSize(1600, 800), Assert.IsType<Bitmap>(overlay.FindControl<Image>("PreviewImage")!.Source).PixelSize);
            Assert.Same(attachment, Assert.Single(host.Composer.Attachments));
            Assert.True(File.Exists(attachment.PayloadPath));
            owner.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            owner.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            AssertNoOverlay(owner);
            Assert.Null(overlay.FindControl<Image>("PreviewImage")!.Source);

            var remove = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => Equals(ToolTip.GetTip(button), "Remove image"));
            await RenderAsync(owner);
            Click(owner, remove);
            Assert.Empty(host.Composer.Attachments);
            Assert.False(File.Exists(attachment.PayloadPath));
            AssertNoOverlay(owner);
        }
        finally
        {
            CloseOverlays(owner);
            host.Composer.ClearAttachments();
            owner.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeline_UserAndReadImagesOpenStoredArtifact(bool toolImage)
    {
        var imageSession = StrongId.NewSessionId();
        var directory = Path.Combine(WorkbenchDataPaths.ArtifactsPath, imageSession.Value);
        var artifacts = new FileArtifactStore(WorkbenchDataPaths.ArtifactsPath);
        byte[] bytes;
        using (UseImageDecoder())
            bytes = CreatePng(1200, 600);
        var artifact = await artifacts.SaveAsync(imageSession, "model-read.png", "image/png", bytes, default);
        var runtime = new FakeAgentRuntime();
        var session = new SessionId("test");
        var state = TestSnapshots.State();
        var projection = runtime.RegisterSession(session, new ConversationSnapshot(0, 1, [], state));
        var store = new ProjectionStore(() => projection, session, InlineUiScheduler.Instance, state);
        using var conversation = new ConversationViewModel(runtime, store);
        using (store)
        {
            ConversationRow row = toolImage
                ? new ToolCallRow("read", new ToolCallId("read-1"), "Read", JsonSerializer.SerializeToElement(new { file_path = "deleted-original.png" }),
                    ToolCallStatus.Succeeded, [new ToolResultMedia(artifact.Uri, "image/png", "model-read.png")], false, null, DateTimeOffset.UtcNow)
                : new UserMessageRow("user", "image", DateTimeOffset.UtcNow, [new FilePart(new PartId("part-1"), artifact.Uri, "image/png", "model-read.png")]);
            conversation.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(), [], Reset: true, ResetRows: [row]));
            var view = new TimelineView { DataContext = conversation };
            var owner = new Window { Width = 900, Height = 600, Content = view };
            owner.Show();
            try
            {
                await RenderAsync(owner);
                var thumbnail = Assert.Single(view.GetVisualDescendants().OfType<ConversationImage>());
                Assert.Equal("model-read.png", thumbnail.FileName);
                Assert.Equal(artifact.Uri, thumbnail.ArtifactUri);
                using (UseImageDecoder())
                {
                    thumbnail.ArtifactUri = null;
                    thumbnail.ArtifactUri = artifact.Uri;
                    await WaitForAsync(() => thumbnail.Source is not null);
                }
                Assert.Equal(new PixelSize(192, 96), Assert.IsType<Bitmap>(thumbnail.Source).PixelSize);
                await RenderAsync(owner);
                ImageViewerOverlay overlay;
                using (UseImageDecoder())
                {
                    Click(owner, thumbnail);
                    overlay = FindOverlay(owner);
                    await WaitForAsync(() => overlay.FindControl<Image>("PreviewImage")!.Source is not null);
                }
                Assert.Equal(new PixelSize(1200, 600), Assert.IsType<Bitmap>(overlay.FindControl<Image>("PreviewImage")!.Source).PixelSize);
                Assert.Equal("model-read.png", overlay.FindControl<TextBlock>("FileNameLabel")!.Text);
                overlay.Close();
                AssertNoOverlay(owner);
                Assert.Null(overlay.FindControl<Image>("PreviewImage")!.Source);
                Assert.NotNull(thumbnail.Source);
            }
            finally
            {
                CloseOverlays(owner);
                owner.Close();
                Directory.Delete(directory, true);
            }
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversationImage_KeyboardActivationAndUnavailableArtifactShowError(bool corrupt)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"alcoler-viewer-{Guid.NewGuid():N}");
        var artifacts = new FileArtifactStore(directory);
        var uri = "alcoler-artifact://image-test/missing.png";
        if (corrupt)
            uri = (await artifacts.SaveAsync(new SessionId("image-test"), "bad.png", "image/png", new byte[] { 1, 2, 3 }, default)).Uri;
        var thumbnail = new ConversationImage(artifacts) { ArtifactUri = uri, MimeType = "image/png", FileName = "unavailable.png" };
        var owner = new Window { Content = thumbnail, Width = 500, Height = 400 };
        owner.Show();
        try
        {
            await RenderAsync(owner);
            Assert.Equal(128, thumbnail.Bounds.Width);
            Assert.Equal(96, thumbnail.Bounds.Height);
            thumbnail.Focus();
            owner.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            owner.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            var overlay = FindOverlay(owner);
            await WaitForAsync(() => overlay.FindControl<TextBlock>("StatusLabel")!.Text != "Loading image...");
            Assert.Null(overlay.FindControl<Image>("PreviewImage")!.Source);
            Assert.True(overlay.FindControl<TextBlock>("StatusLabel")!.IsVisible);
            Assert.False(overlay.FindControl<StackPanel>("ZoomControls")!.IsEnabled);
            Assert.True(overlay.FindControl<Button>("CloseImageButton")!.IsEffectivelyEnabled);
            await RenderAsync(owner);
            Click(owner, overlay.FindControl<Button>("CloseImageButton")!);
            AssertNoOverlay(owner);
        }
        finally
        {
            CloseOverlays(owner);
            owner.Close();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task Viewer_LocalFileRetainsFullResolutionAndNameAndReleasesResources()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"alcoler-viewer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "local screenshot.png");
        var owner = new Window();
        owner.Show();
        try
        {
            using var scope = UseImageDecoder();
            await File.WriteAllBytesAsync(path, CreatePng(1600, 800));
            ImageViewerOverlay overlay;
            Task<bool> closed;
            using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                closed = ImageViewerOverlay.TryShowFileAsync(owner, path, "IMAGE/PNG");
                overlay = FindOverlay(owner);
                await WaitForAsync(() => overlay.FindControl<Image>("PreviewImage")!.Source is not null);
            }
            var image = overlay.FindControl<Image>("PreviewImage")!;
            var bitmap = Assert.IsType<Bitmap>(image.Source);
            Assert.Equal(new PixelSize(1600, 800), bitmap.PixelSize);
            Assert.Equal("local screenshot.png", overlay.FindControl<TextBlock>("FileNameLabel")!.Text);
            Assert.False(overlay.FindControl<TextBlock>("StatusLabel")!.IsVisible);
            Assert.True(overlay.FindControl<StackPanel>("ZoomControls")!.IsEnabled);
            using var reopened = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(reopened.Length > 0);
            overlay.Close();
            Assert.True(await closed);
            AssertNoOverlay(owner);
            Assert.Null(image.Source);
            using var output = new MemoryStream();
            Assert.NotNull(Record.Exception(() => bitmap.Save(output)));
        }
        finally
        {
            CloseOverlays(owner);
            owner.Close();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaTheory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("corrupt")]
    [InlineData("oversized-bytes")]
    [InlineData("oversized-pixels")]
    public async Task Viewer_InvalidLocalFileRequestsFallbackAndReleasesResources(string failure)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"alcoler-viewer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "unavailable.png");
        var owner = new Window();
        owner.Show();
        try
        {
            using var scope = UseImageDecoder();
            switch (failure)
            {
                case "empty":
                    await File.WriteAllBytesAsync(path, []);
                    break;
                case "corrupt":
                    await File.WriteAllBytesAsync(path, [1, 2, 3]);
                    break;
                case "oversized-bytes":
                    using (var file = File.Create(path))
                        file.SetLength(ComposerImageAttachment.MaxImageBytes + 1L);
                    break;
                case "oversized-pixels":
                    var bytes = CreatePng();
                    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 4097);
                    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 4096);
                    await File.WriteAllBytesAsync(path, bytes);
                    break;
            }
            var closed = ImageViewerOverlay.TryShowFileAsync(owner, path, "image/png");
            await WaitForAsync(() => closed.IsCompleted);
            Assert.False(await closed);
            AssertNoOverlay(owner);
            if (File.Exists(path))
            {
                using var reopened = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.True(reopened.CanRead);
            }
        }
        finally
        {
            CloseOverlays(owner);
            owner.Close();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task Viewer_CloseLocalFileDuringLoadDoesNotRequestFallback()
    {
        var directory = Directory.CreateTempSubdirectory("alcoler-viewer-cancel-").FullName;
        var owner = new Window();
        owner.Show();
        try
        {
            using var scope = UseImageDecoder();
            var path = Path.Combine(directory, "cancelled.png");
            await File.WriteAllBytesAsync(path, CreatePng());

            var closed = ImageViewerOverlay.TryShowFileAsync(owner, path, "image/png");
            var overlay = FindOverlay(owner);
            overlay.Close();

            Assert.True(await closed);
            AssertNoOverlay(owner);
            Assert.Null(overlay.FindControl<Image>("PreviewImage")!.Source);
        }
        finally
        {
            CloseOverlays(owner);
            owner.Close();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task Viewer_FitZoomActualSizeAndResizeKeepFullResolution()
    {
        Bitmap bitmap;
        using (UseImageDecoder())
            bitmap = ComposerImageAttachment.DecodeThumbnail(CreatePng(2000, 1000), "image/png", 32_768);
        var owner = new Window();
        owner.Show();
        var closed = ImageViewerOverlay.ShowAsync(owner, "large.png", _ => Task.FromResult<Bitmap?>(bitmap));
        var overlay = FindOverlay(owner);
        try
        {
            await RenderAsync(owner);
            var image = overlay.FindControl<Image>("PreviewImage")!;
            var scroll = overlay.FindControl<ScrollViewer>("ImageScroll")!;
            Assert.Same(bitmap, image.Source);
            Assert.InRange(image.Width, 1, scroll.Viewport.Width);
            Assert.InRange(image.Height, 1, scroll.Viewport.Height);
            var fittedWidth = image.Width;
            Click(owner, overlay.FindControl<Button>("ZoomInButton")!);
            Assert.True(image.Width > fittedWidth);
            await RenderAsync(owner);
            Click(owner, overlay.FindControl<Button>("ActualSizeButton")!);
            Assert.Equal(2000, image.Width);
            Assert.Equal(1000, image.Height);
            await RenderAsync(owner);
            Assert.True(scroll.Extent.Width > scroll.Viewport.Width);
            Click(owner, overlay.FindControl<Button>("FitButton")!);
            await RenderAsync(owner);
            Assert.InRange(image.Width, 1, scroll.Viewport.Width);
            owner.Width = 720;
            await RenderAsync(owner);
            Assert.True(image.Width < fittedWidth);
            Assert.InRange(image.Width, 1, scroll.Viewport.Width);
            Assert.Equal(new PixelSize(2000, 1000), bitmap.PixelSize);
            owner.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            owner.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await closed;
            Assert.Null(image.Source);
        }
        finally
        {
            CloseOverlays(owner);
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Viewer_CloseDuringLoadCancelsAndDisposesLateBitmap()
    {
        var completion = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var owner = new Window();
        owner.Show();
        var closed = ImageViewerOverlay.ShowAsync(owner, "late.png", cancellation =>
        {
            token = cancellation;
            return completion.Task;
        });
        var overlay = FindOverlay(owner);
        Bitmap bitmap;
        using (UseImageDecoder())
            bitmap = ComposerImageAttachment.DecodeThumbnail(CreatePng(), "image/png");
        overlay.Close();
        await closed;
        Assert.True(token.IsCancellationRequested);
        completion.SetResult(bitmap);
        await WaitForAsync(() =>
        {
            using var output = new MemoryStream();
            return Record.Exception(() => bitmap.Save(output)) is not null;
        });
        Assert.Null(overlay.FindControl<Image>("PreviewImage")!.Source);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Viewer_ClickOnImageKeepsOpenAndClickOnBackdropCloses()
    {
        Bitmap bitmap;
        using (UseImageDecoder())
            bitmap = ComposerImageAttachment.DecodeThumbnail(CreatePng(400, 200), "image/png", 32_768);
        var owner = new Window { Width = 900, Height = 600 };
        owner.Show();
        var closed = ImageViewerOverlay.ShowAsync(owner, "backdrop.png", _ => Task.FromResult<Bitmap?>(bitmap));
        var overlay = FindOverlay(owner);
        Assert.Empty(owner.OwnedWindows);
        try
        {
            await RenderAsync(owner);
            var image = overlay.FindControl<Image>("PreviewImage")!;
            Assert.NotNull(image.Source);
            var imagePoint = image.TranslatePoint(new Point(image.Bounds.Width / 2, image.Bounds.Height / 2), owner)!.Value;
            owner.MouseMove(imagePoint);
            owner.MouseDown(imagePoint, MouseButton.Left);
            owner.MouseUp(imagePoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            FindOverlay(owner);
            Assert.False(closed.IsCompleted);

            var backdropPoint = new Point(4, owner.ClientSize.Height - 4);
            owner.MouseMove(backdropPoint);
            owner.MouseDown(backdropPoint, MouseButton.Left);
            owner.MouseUp(backdropPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            await closed.WaitAsync(TimeSpan.FromSeconds(5));
            AssertNoOverlay(owner);
            Assert.Null(image.Source);
        }
        finally
        {
            CloseOverlays(owner);
            owner.Close();
        }
    }

    private static IDisposable UseImageDecoder()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Avalonia.Media.GlyphRun).TypeHandle);
        var scope = AvaloniaLocator.EnterScope();
        SkiaPlatform.Initialize();
        return scope;
    }

    private static byte[] CreatePng(int width = 4, int height = 2)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        bitmap.Erase(SkiaSharp.SKColors.CornflowerBlue);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task RenderAsync(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var rendered = ElementComposition.GetElementVisual(window)!.Compositor.RequestCompositionBatchCommitAsync().Rendered;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await rendered.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(point));
        Assert.True(ReferenceEquals(hit, control) || hit.GetVisualAncestors().Contains(control)
            || control.GetVisualAncestors().Contains(hit));
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static ImageViewerOverlay FindOverlay(Window owner) =>
        Assert.Single(owner.GetVisualDescendants().OfType<ImageViewerOverlay>());

    private static void AssertNoOverlay(Window owner) =>
        Assert.Empty(owner.GetVisualDescendants().OfType<ImageViewerOverlay>());

    private static void CloseOverlays(Window owner)
    {
        foreach (var overlay in owner.GetVisualDescendants().OfType<ImageViewerOverlay>().ToArray())
            overlay.Close();
    }
}
