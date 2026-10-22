using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Stores;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Alcoler.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using Xunit;

namespace Alcoler.Tests;

public sealed class ClipboardImagesTests
{
    [AvaloniaTheory]
    [InlineData("image/png")]
    [InlineData("PNG")]
    public async Task ReadAsync_ExposedPng_PreservesPayloadAndUsesSmallThumbnail(string format)
    {
        using var scope = UseImageDecoder();
        var png = CreatePng(640, 320);
        var clipboard = new FakeClipboard((format, png), (DataFormats.Text, "plain text"));
        var fallbackCalls = 0;

        var attachments = await ComposerClipboardImages.ReadAsync(clipboard, () =>
        {
            fallbackCalls++;
            return Task.FromResult<byte[]?>(null);
        });

        using var attachment = Assert.Single(attachments);
        Assert.Equal("image/png", attachment.Image.MimeType);
        Assert.Equal(png, Convert.FromBase64String(attachment.Image.Base64Data));
        Assert.Equal("Clipboard image.png", attachment.Name);
        Assert.Equal(new PixelSize(160, 80), attachment.Thumbnail.PixelSize);
        Assert.Equal(0, fallbackCalls);
        Assert.Equal(new[] { format }, clipboard.RequestedFormats);
    }

    [AvaloniaFact]
    public async Task ReadAsync_JpegStream_PreservesJpegAndLeavesStreamOpen()
    {
        using var scope = UseImageDecoder();
        using var image = SkiaSharp.SKImage.FromEncodedData(CreatePng(320, 640));
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
        var bytes = encoded.ToArray();
        using var stream = new MemoryStream(bytes);
        var clipboard = new FakeClipboard(("image/jpeg", stream));

        using var attachment = Assert.Single(await ReadAsync(clipboard));

        Assert.Equal("image/jpeg", attachment.Image.MimeType);
        Assert.Equal(bytes, Convert.FromBase64String(attachment.Image.Base64Data));
        Assert.Equal(new PixelSize(80, 160), attachment.Thumbnail.PixelSize);
        Assert.True(stream.CanRead);
    }

    [AvaloniaFact]
    public async Task ReadAsync_MultipleRepresentations_ChoosesOnePng()
    {
        using var scope = UseImageDecoder();
        var bytes = CreatePng();
        var clipboard = new FakeClipboard(("PNG", bytes), ("image/png", bytes), ("image/jpeg", Array.Empty<byte>()));

        using var attachment = Assert.Single(await ReadAsync(clipboard));

        Assert.Equal("image/png", attachment.Image.MimeType);
        Assert.Equal(new[] { "image/png" }, clipboard.RequestedFormats);
    }

    [AvaloniaFact]
    public async Task ReadAsync_RealClipboardDataObject_ReadsPng()
    {
        using var scope = UseImageDecoder();
        var clipboard = new Window().Clipboard!;
        var data = new DataObject();
        var png = CreatePng();
        data.Set("image/png", png);
        data.Set(DataFormats.Text, "text alongside image");
        await clipboard.SetDataObjectAsync(data);
        try
        {
            using var attachment = Assert.Single(await ComposerClipboardImages.ReadAsync(clipboard));
            Assert.Equal(png, Convert.FromBase64String(attachment.Image.Base64Data));
            Assert.Equal("text alongside image", await clipboard.GetTextAsync());
        }
        finally
        {
            await clipboard.ClearAsync();
        }
    }

    [AvaloniaFact]
    public async Task ReadAsync_TextOnly_ReturnsEmptyWithoutReadingText()
    {
        var clipboard = new FakeClipboard((DataFormats.Text, "ordinary text"));

        Assert.Empty(await ReadAsync(clipboard));
        Assert.Empty(clipboard.RequestedFormats);
    }

    [AvaloniaFact]
    public async Task ReadAsync_Files_ReadsImagesAndIgnoresNonImageFiles()
    {
        using var scope = UseImageDecoder();
        var png = CreatePng();
        var first = new FakeFile("first.png", () => new MemoryStream(png));
        var ignored = new FakeFile("notes.txt", () => throw new InvalidOperationException());
        var second = new FakeFile("second.PNG", () => new MemoryStream(png));
        var clipboard = new FakeClipboard((DataFormats.Files, new IStorageItem[] { first, ignored, second }));

        var attachments = await ReadAsync(clipboard);
        try
        {
            Assert.Equal(new[] { "first.png", "second.PNG" }, attachments.Select(attachment => attachment.Name));
            Assert.All(attachments, attachment => Assert.Equal(png, Convert.FromBase64String(attachment.Image.Base64Data)));
            Assert.False(ignored.WasOpened);
        }
        finally
        {
            foreach (var attachment in attachments)
                attachment.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task ReadAsync_OversizedFile_RejectsBeforeOpening()
    {
        var file = new FakeFile("large.png", () => throw new InvalidOperationException(), ComposerImageAttachment.MaxImageBytes + 1ul);
        var clipboard = new FakeClipboard((DataFormats.Files, new IStorageItem[] { file }));

        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(clipboard));
        Assert.False(file.WasOpened);
    }

    [AvaloniaFact]
    public async Task ReadAsync_OversizedSeekableStream_RejectsBeforeReading()
    {
        using var stream = new SizeOnlyStream(ComposerImageAttachment.MaxImageBytes + 1L);
        var clipboard = new FakeClipboard(("image/png", stream));

        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(clipboard));
        Assert.False(stream.WasRead);
    }

    [AvaloniaFact]
    public async Task ReadAsync_EmptyOrInvalidImages_ThrowInvalidData()
    {
        using var scope = UseImageDecoder();
        foreach (var bytes in new byte[][] { [], [1, 2, 3], [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a] })
            await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(new FakeClipboard(("image/png", bytes))));
    }

    [AvaloniaFact]
    public async Task ReadAsync_ValidPngHeaderWithoutImageData_ThrowsInvalidData()
    {
        using var scope = UseImageDecoder();
        var header = CreatePng()[..33];

        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(new FakeClipboard(("image/png", header))));
    }

    [AvaloniaTheory]
    [InlineData("CF_DIB")]
    [InlineData("Unknown_Format_8")]
    public async Task ReadAsync_ExposedDib_ConvertsToPng(string format)
    {
        using var scope = UseImageDecoder();
        var clipboard = new FakeClipboard((format, CreateDib()));

        using var attachment = Assert.Single(await ReadAsync(clipboard));

        Assert.Equal("image/png", attachment.Image.MimeType);
        using var image = new Bitmap(new MemoryStream(Convert.FromBase64String(attachment.Image.Base64Data)));
        Assert.Equal(new PixelSize(2, 2), image.PixelSize);
    }

    [AvaloniaFact]
    public async Task ReadAsync_NativeFallback_ConvertsDibToPng()
    {
        using var scope = UseImageDecoder();
        var clipboard = new FakeClipboard(("Unknown_Format_2", new object()));

        using var attachment = Assert.Single(await ComposerClipboardImages.ReadAsync(clipboard, () => Task.FromResult<byte[]?>(CreateDib())));

        Assert.Equal("image/png", attachment.Image.MimeType);
        Assert.Equal("Clipboard image.png", attachment.Name);
        Assert.InRange(attachment.Thumbnail.PixelSize.Width, 1, 160);
        Assert.InRange(attachment.Thumbnail.PixelSize.Height, 1, 160);
    }

    [AvaloniaTheory]
    [InlineData(640, 320)]
    [InlineData(320, 640)]
    public void Attachment_WebpThumbnailBoundsBothDimensions(int width, int height)
    {
        using var scope = UseImageDecoder();
        using var image = SkiaSharp.SKImage.FromEncodedData(CreatePng(width, height));
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Webp, 90);
        using var attachment = ComposerImageAttachment.FromBytes(data.ToArray(), "image/webp", "image.webp");

        Assert.InRange(attachment.Thumbnail.PixelSize.Width, 1, 160);
        Assert.InRange(attachment.Thumbnail.PixelSize.Height, 1, 160);
    }

    [AvaloniaFact]
    public void Attachment_ExcessiveHeaderDimensions_RejectsBeforeDecode()
    {
        var png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }.CopyTo(png, 0);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), 13);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 100_000);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 100_000);

        var exception = Assert.Throws<InvalidDataException>(() => ComposerImageAttachment.FromBytes(png, "image/png", "huge.png"));

        Assert.Contains("megapixels", exception.Message);
    }

    [AvaloniaFact]
    public async Task Composer_PasteImageThenText_RetainsDraftAndSelectionSemantics()
    {
        byte[] png;
        using (UseImageDecoder())
            png = CreatePng(640, 320);
        var host = new DraftConversationViewModel(Environment.CurrentDirectory, null, _ => Task.CompletedTask);
        var view = new ComposerView { DataContext = host };
        var window = new Window { Content = view };
        window.Show();
        var clipboard = window.Clipboard!;
        try
        {
            var data = new DataObject();
            data.Set("image/png", png);
            data.Set(DataFormats.Text, "not pasted with image");
            await clipboard.SetDataObjectAsync(data);
            var input = view.FindControl<TextBox>("InputBox")!;
            var shell = view.FindControl<Border>("ComposerShell")!;
            window.UpdateLayout();
            var compactHeight = shell.Bounds.Height;
            var inputInset = shell.Padding.Top + shell.BorderThickness.Top;
            Assert.Equal(inputInset, input.TranslatePoint(default, shell)!.Value.Y);
            host.Composer.Draft = "unchanged draft";
            input.Paste();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            Assert.Equal("unchanged draft", host.Composer.Draft);
            Assert.Equal(png, Convert.FromBase64String(Assert.Single(host.Composer.Attachments).Image.Base64Data));
            window.UpdateLayout();
            Assert.True(shell.Bounds.Height > compactHeight);
            Assert.True(input.TranslatePoint(default, shell)!.Value.Y > inputInset);
            host.Composer.ClearAttachments();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            Assert.Equal(compactHeight, shell.Bounds.Height);
            Assert.Equal(inputInset, input.TranslatePoint(default, shell)!.Value.Y);
            await clipboard.SetTextAsync("replacement");
            host.Composer.Draft = "before after";
            input.SelectionStart = 7;
            input.SelectionEnd = 12;
            input.Paste();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            Assert.Equal("before replacement", host.Composer.Draft);
            Assert.Empty(host.Composer.Attachments);
        }
        finally
        {
            host.Composer.ClearAttachments();
            await clipboard.ClearAsync();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Conversation_RejectedImageSendRetainsAttachmentAndAcceptedRetryDeletesPayload()
    {
        using var scope = UseImageDecoder();
        var (viewModel, runtime, store) = CreateConversation();
        using (viewModel)
        using (store)
        {
            var png = CreatePng();
            var attachment = ComposerImageAttachment.FromBytes(png, "image/png", "screenshot.png");
            viewModel.Composer.AddAttachments([attachment]);
            runtime.RejectNextCommand = true;

            await viewModel.SendAsync();

            Assert.Same(attachment, Assert.Single(viewModel.Composer.Attachments));
            Assert.True(File.Exists(attachment.PayloadPath));
            Assert.NotNull(viewModel.Banner);
            var rejected = Assert.IsType<SendTextCommand>(Assert.Single(runtime.Submitted));
            Assert.Equal(string.Empty, rejected.Text);
            Assert.Equal(png, Convert.FromBase64String(Assert.Single(rejected.Images!).Base64Data));
            runtime.RejectNextCommand = false;

            await viewModel.SendAsync();

            Assert.Equal(2, runtime.Submitted.Count);
            var accepted = Assert.IsType<SendTextCommand>(runtime.Submitted[1]);
            Assert.Equal(png, Convert.FromBase64String(Assert.Single(accepted.Images!).Base64Data));
            Assert.Empty(viewModel.Composer.Attachments);
            Assert.False(File.Exists(attachment.PayloadPath));
        }
    }

    [AvaloniaFact]
    public async Task Composer_PastedImageSendButton_SubmitsImageOnlyCommandAndDeletesPayload()
    {
        byte[] png;
        using (UseImageDecoder())
            png = CreatePng(640, 320);
        var (viewModel, runtime, store) = CreateConversation();
        using (viewModel)
        using (store)
        {
            var view = new ComposerView { DataContext = viewModel };
            var window = new Window { Content = view, Width = 900, Height = 600 };
            window.Show();
            var clipboard = window.Clipboard!;
            try
            {
                var data = new DataObject();
                data.Set("image/png", png);
                await clipboard.SetDataObjectAsync(data);
                view.FindControl<TextBox>("InputBox")!.Paste();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                var attachment = Assert.Single(viewModel.Composer.Attachments);
                var path = attachment.PayloadPath;
                var send = view.FindControl<Button>("SendButton")!;
                Assert.True(send.IsEnabled);
                window.UpdateLayout();
                var rendered = ElementComposition.GetElementVisual(window)!.Compositor.RequestCompositionBatchCommitAsync().Rendered;
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                await rendered.WaitAsync(TimeSpan.FromSeconds(5));
                var point = send.TranslatePoint(new Point(send.Bounds.Width / 2, send.Bounds.Height / 2), window)!.Value;
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var command = Assert.IsType<SendTextCommand>(Assert.Single(runtime.Submitted));
                Assert.Equal(string.Empty, command.Text);
                Assert.Equal(png, Convert.FromBase64String(Assert.Single(command.Images!).Base64Data));
                Assert.Empty(viewModel.Composer.Attachments);
                Assert.False(File.Exists(path));
            }
            finally
            {
                await clipboard.ClearAsync();
                window.Close();
            }
        }
    }

    [Fact]
    public void CreateBitmapFile_AddsCorrectHeadersAndBitfieldOffsets()
    {
        foreach (var headerSize in new[] { 40, 108, 124 })
        {
            var dib = CreateDib(headerSize);
            var bitmap = WindowsClipboardImages.CreateBitmapFile(dib);

            Assert.Equal((byte)'B', bitmap[0]);
            Assert.Equal((byte)'M', bitmap[1]);
            Assert.Equal(bitmap.Length, (int)BinaryPrimitives.ReadUInt32LittleEndian(bitmap.AsSpan(2)));
            Assert.Equal(headerSize + 14, (int)BinaryPrimitives.ReadUInt32LittleEndian(bitmap.AsSpan(10)));
            Assert.Equal(dib, bitmap[14..]);
        }
        var bitfields = CreateDib(extraMasks: true);
        Assert.Equal(66u, BinaryPrimitives.ReadUInt32LittleEndian(WindowsClipboardImages.CreateBitmapFile(bitfields).AsSpan(10)));
    }

    [Fact]
    public void CreateBitmapFile_RejectsTruncatedOrExcessiveDimensions()
    {
        Assert.Throws<InvalidDataException>(() => WindowsClipboardImages.CreateBitmapFile(new byte[12]));
        var dib = CreateDib();
        Assert.Throws<InvalidDataException>(() => WindowsClipboardImages.CreateBitmapFile(dib.AsSpan(0, dib.Length - 1)));
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), int.MinValue);
        Assert.Throws<InvalidDataException>(() => WindowsClipboardImages.CreateBitmapFile(dib));
    }

    [AvaloniaFact]
    public void Attachment_PayloadIsTempBackedAndDeletedOnDispose()
    {
        using var scope = UseImageDecoder();
        var png = CreatePng();
        var attachment = ComposerImageAttachment.FromBytes(png, "image/png", "example.png");
        var path = attachment.PayloadPath;

        Assert.True(File.Exists(path));
        Assert.NotSame(attachment.Image, attachment.Image);
        Assert.Equal(attachment.Image, attachment.Image);
        attachment.Dispose();

        Assert.False(File.Exists(path));
        Assert.Throws<ObjectDisposedException>(() => attachment.Image);
        attachment.Dispose();
    }

    [AvaloniaFact]
    public void Composer_ImagesOnlyCanSendAndRemovingAttachmentDeletesPayload()
    {
        using var scope = UseImageDecoder();
        var composer = new ComposerViewModel { IsTurnActive = true };
        var attachment = ComposerImageAttachment.FromBytes(CreatePng(), "image/png", "example.png");
        var path = attachment.PayloadPath;
        composer.AddAttachments([attachment]);

        Assert.True(composer.CanSend);
        Assert.False(composer.ShowStop);
        composer.RemoveAttachment(attachment);

        Assert.False(File.Exists(path));
        Assert.Empty(composer.Attachments);
        Assert.False(composer.CanSend);
        Assert.True(composer.ShowStop);
    }

    [AvaloniaFact]
    public void Composer_SendSnapshotClearsOnlySubmittedAttachmentEvenWithIdenticalBytes()
    {
        using var scope = UseImageDecoder();
        var composer = new ComposerViewModel();
        var png = CreatePng();
        var submitted = ComposerImageAttachment.FromBytes(png, "image/png", "first.png");
        var pending = ComposerImageAttachment.FromBytes(png, "image/png", "second.png");
        composer.AddAttachments([submitted]);
        var images = composer.SnapshotImages();
        composer.AddAttachments([pending]);
        try
        {
            composer.ClearIfSendable(string.Empty, images);

            Assert.Same(pending, Assert.Single(composer.Attachments));
            Assert.False(File.Exists(submitted.PayloadPath));
            Assert.True(File.Exists(pending.PayloadPath));
        }
        finally
        {
            composer.ClearAttachments();
        }
    }

    [AvaloniaTheory]
    [InlineData(640, 320)]
    [InlineData(320, 640)]
    [InlineData(1, 640)]
    [InlineData(640, 1)]
    public void Attachment_ThumbnailStaysBoundedAndDisposeIsIdempotent(int width, int height)
    {
        using var scope = UseImageDecoder();
        var png = CreatePng(width, height);
        var attachment = ComposerImageAttachment.FromBytes(png, "image/png", "example.png");

        Assert.InRange(attachment.Thumbnail.PixelSize.Width, 1, 160);
        Assert.InRange(attachment.Thumbnail.PixelSize.Height, 1, 160);
        Assert.Equal("example.png", attachment.Name);
        Assert.Equal(png, Convert.FromBase64String(attachment.Image.Base64Data));
        attachment.Dispose();
        attachment.Dispose();
    }

    private static (ConversationViewModel ViewModel, FakeAgentRuntime Runtime, ProjectionStore Store) CreateConversation()
    {
        var runtime = new FakeAgentRuntime();
        var sessionId = StrongId.NewSessionId();
        var state = TestSnapshots.State();
        var projection = runtime.RegisterSession(sessionId, new ConversationSnapshot(0, 1, [], state));
        var store = new ProjectionStore(() => projection, sessionId, InlineUiScheduler.Instance, state);
        var viewModel = new ConversationViewModel(runtime, store);
        viewModel.ConfigureModel(new ModelChoice("test", "vision", "Vision", new ModelProperties(SupportsImage: true)));
        return (viewModel, runtime, store);
    }

    private static Task<IReadOnlyList<ComposerImageAttachment>> ReadAsync(IClipboard clipboard) =>
        ComposerClipboardImages.ReadAsync(clipboard, () => Task.FromResult<byte[]?>(null));

    private static IDisposable UseImageDecoder()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Avalonia.Media.GlyphRun).TypeHandle);
        var scope = AvaloniaLocator.EnterScope();
        SkiaPlatform.Initialize();
        return scope;
    }

    private static byte[] CreatePng(int width = 4, int height = 2)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var framebuffer = bitmap.Lock())
        {
            var row = new byte[framebuffer.RowBytes];
            for (var x = 0; x < width; x++)
            {
                row[x * 4 + 2] = 255;
                row[x * 4 + 3] = 255;
            }
            for (var y = 0; y < height; y++)
                Marshal.Copy(row, 0, framebuffer.Address + y * framebuffer.RowBytes, row.Length);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    private static byte[] CreateDib(int headerSize = 40, bool extraMasks = false)
    {
        var offset = headerSize + (extraMasks ? 12 : 0);
        var dib = new byte[offset + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(dib, (uint)headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(20), 16);
        if (extraMasks)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(40), 0x00ff0000);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(44), 0x0000ff00);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(48), 0x000000ff);
        }
        for (var index = offset; index < dib.Length; index += 4)
        {
            dib[index + 2] = 255;
            dib[index + 3] = 255;
        }
        return dib;
    }

    private sealed class FakeClipboard(params (string Format, object Value)[] values) : IClipboard
    {
        private readonly Dictionary<string, object> _values = values.ToDictionary(value => value.Format, value => value.Value);
        public List<string> RequestedFormats { get; } = [];
        public Task<string[]> GetFormatsAsync() => Task.FromResult(_values.Keys.ToArray());
        public Task<object?> GetDataAsync(string format)
        {
            RequestedFormats.Add(format);
            return Task.FromResult(_values.GetValueOrDefault(format));
        }
        public Task<string?> GetTextAsync() => throw new InvalidOperationException();
        public Task SetTextAsync(string? text) => throw new InvalidOperationException();
        public Task ClearAsync() => throw new InvalidOperationException();
        public Task SetDataObjectAsync(IDataObject data) => throw new InvalidOperationException();
        public Task FlushAsync() => throw new InvalidOperationException();
        public Task<IDataObject?> TryGetInProcessDataObjectAsync() => Task.FromResult<IDataObject?>(null);
    }

    private sealed class FakeFile(string name, Func<Stream> open, ulong? size = null) : IStorageFile
    {
        public bool WasOpened { get; private set; }
        public string Name => name;
        public Uri Path => new($"file:///clipboard/{name}");
        public bool CanBookmark => false;
        public Task<StorageItemProperties> GetBasicPropertiesAsync() => Task.FromResult(new StorageItemProperties(size));
        public Task<Stream> OpenReadAsync()
        {
            WasOpened = true;
            return Task.FromResult(open());
        }
        public Task<Stream> OpenWriteAsync() => throw new NotSupportedException();
        public Task<string?> SaveBookmarkAsync() => Task.FromResult<string?>(null);
        public Task<IStorageFolder?> GetParentAsync() => Task.FromResult<IStorageFolder?>(null);
        public Task DeleteAsync() => throw new NotSupportedException();
        public Task<IStorageItem?> MoveAsync(IStorageFolder destination) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class SizeOnlyStream(long size) : Stream
    {
        public bool WasRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => size;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            WasRead = true;
            throw new InvalidOperationException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
