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
            ImageViewerDialog dialog;
            using (UseImageDecoder())
            {
                Click(owner, preview);
                dialog = Assert.IsType<ImageViewerDialog>(Assert.Single(owner.OwnedWindows));
                await WaitForAsync(() => dialog.FindControl<Image>("PreviewImage")!.Source is not null);
            }
            Assert.Equal("screenshot.png", dialog.Title);
            Assert.Equal(new PixelSize(1600, 800), Assert.IsType<Bitmap>(dialog.FindControl<Image>("PreviewImage")!.Source).PixelSize);
            Assert.Same(attachment, Assert.Single(host.Composer.Attachments));
            Assert.True(File.Exists(attachment.PayloadPath));
            dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            owner.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.Empty(owner.OwnedWindows);
            Assert.Null(dialog.FindControl<Image>("PreviewImage")!.Source);

            var remove = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => Equals(ToolTip.GetTip(button), "Remove image"));
            await RenderAsync(owner);
            Click(owner, remove);
            Assert.Empty(host.Composer.Attachments);
            Assert.False(File.Exists(attachment.PayloadPath));
            Assert.Empty(owner.OwnedWindows);
        }
        finally
        {
            CloseOwnedWindows(owner);
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
                ImageViewerDialog dialog;
                using (UseImageDecoder())
                {
                    Click(owner, thumbnail);
                    dialog = Assert.IsType<ImageViewerDialog>(Assert.Single(owner.OwnedWindows));
                    await WaitForAsync(() => dialog.FindControl<Image>("PreviewImage")!.Source is not null);
                }
                Assert.Equal(new PixelSize(1200, 600), Assert.IsType<Bitmap>(dialog.FindControl<Image>("PreviewImage")!.Source).PixelSize);
                Assert.Equal("model-read.png", dialog.Title);
                dialog.Close();
                Assert.Null(dialog.FindControl<Image>("PreviewImage")!.Source);
                Assert.NotNull(thumbnail.Source);
            }
            finally
            {
                CloseOwnedWindows(owner);
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
            var dialog = Assert.IsType<ImageViewerDialog>(Assert.Single(owner.OwnedWindows));
            await WaitForAsync(() => dialog.FindControl<TextBlock>("StatusLabel")!.Text != "Loading image...");
            Assert.Null(dialog.FindControl<Image>("PreviewImage")!.Source);
            Assert.True(dialog.FindControl<TextBlock>("StatusLabel")!.IsVisible);
            Assert.False(dialog.FindControl<StackPanel>("ZoomControls")!.IsEnabled);
            Assert.True(dialog.FindControl<Button>("CloseImageButton")!.IsEffectivelyEnabled);
            await RenderAsync(dialog);
            Click(dialog, dialog.FindControl<Button>("CloseImageButton")!);
            Assert.Empty(owner.OwnedWindows);
        }
        finally
        {
            CloseOwnedWindows(owner);
            owner.Close();
            if (Directory.Exists(directory))
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
        var dialog = new ImageViewerDialog("large.png", _ => Task.FromResult<Bitmap?>(bitmap));
        var closed = dialog.ShowDialog(owner);
        try
        {
            await RenderAsync(dialog);
            var image = dialog.FindControl<Image>("PreviewImage")!;
            var scroll = dialog.FindControl<ScrollViewer>("ImageScroll")!;
            Assert.Same(bitmap, image.Source);
            Assert.InRange(image.Width, 1, scroll.Viewport.Width);
            Assert.InRange(image.Height, 1, scroll.Viewport.Height);
            var fittedWidth = image.Width;
            Click(dialog, dialog.FindControl<Button>("ZoomInButton")!);
            Assert.True(image.Width > fittedWidth);
            await RenderAsync(dialog);
            Click(dialog, dialog.FindControl<Button>("ActualSizeButton")!);
            Assert.Equal(2000, image.Width);
            Assert.Equal(1000, image.Height);
            await RenderAsync(dialog);
            Assert.True(scroll.Extent.Width > scroll.Viewport.Width);
            Click(dialog, dialog.FindControl<Button>("FitButton")!);
            await RenderAsync(dialog);
            Assert.InRange(image.Width, 1, scroll.Viewport.Width);
            dialog.Width = 720;
            await RenderAsync(dialog);
            Assert.True(image.Width < fittedWidth);
            Assert.InRange(image.Width, 1, scroll.Viewport.Width);
            Assert.Equal(new PixelSize(2000, 1000), bitmap.PixelSize);
            dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            owner.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await closed;
            Assert.Null(image.Source);
        }
        finally
        {
            CloseOwnedWindows(owner);
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
        var dialog = new ImageViewerDialog("late.png", cancellation =>
        {
            token = cancellation;
            return completion.Task;
        });
        var closed = dialog.ShowDialog(owner);
        Bitmap bitmap;
        using (UseImageDecoder())
            bitmap = ComposerImageAttachment.DecodeThumbnail(CreatePng(), "image/png");
        dialog.Close();
        await closed;
        Assert.True(token.IsCancellationRequested);
        completion.SetResult(bitmap);
        await WaitForAsync(() =>
        {
            using var output = new MemoryStream();
            return Record.Exception(() => bitmap.Save(output)) is not null;
        });
        Assert.Null(dialog.FindControl<Image>("PreviewImage")!.Source);
        owner.Close();
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
        Assert.True(ReferenceEquals(hit, control) || hit.GetVisualAncestors().Contains(control));
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void CloseOwnedWindows(Window owner)
    {
        foreach (var window in owner.OwnedWindows.ToArray())
            window.Close();
    }
}
