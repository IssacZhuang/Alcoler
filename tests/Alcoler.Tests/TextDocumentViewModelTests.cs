using System.Collections.Concurrent;
using System.Text;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Xunit;

namespace Alcoler.Tests;

public sealed class TextDocumentViewModelTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("alcoler-text-documents-").FullName;

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    [InlineData("utf32-le")]
    [InlineData("utf32-be")]
    public async Task Reload_DecodesDefaultUtf8AndUnicodeBoms(string encodingName)
    {
        Encoding encoding = encodingName switch
        {
            "utf8" => new UTF8Encoding(false),
            "utf8-bom" => new UTF8Encoding(true),
            "utf16-le" => new UnicodeEncoding(false, true),
            "utf16-be" => new UnicodeEncoding(true, true),
            "utf32-le" => new UTF32Encoding(false, true),
            _ => new UTF32Encoding(true, true),
        };
        const string content = "A readable file\r\nUnicode: café 中文";
        var path = Path.Combine(_root, "preview.txt");
        await File.WriteAllBytesAsync(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(content)]);
        using var preview = TextDocumentViewModel.ForFile(path, InlineUiScheduler.Instance);

        await preview.ReloadAsync();

        Assert.Equal(content, preview.Text);
        Assert.Null(preview.Error);
        Assert.False(preview.IsLoading);
        Assert.Equal(path, preview.SourceLabel);
        Assert.True(preview.CanReload);
    }

    [Fact]
    public async Task Reload_BoundsActualStreamWithoutLengthOrSeeking()
    {
        using var stream = new EndlessTextStream();
        using var preview = new TextDocumentViewModel("large.txt", InlineUiScheduler.Instance,
            _ => Task.FromResult<Stream>(stream));

        await preview.ReloadAsync();

        Assert.Equal(TextDocumentViewModel.MaxFileBytes + 1, stream.BytesRead);
        Assert.Contains($"{TextDocumentViewModel.MaxFileBytes / 1024} KiB", preview.Error);
        Assert.Empty(preview.Text);
        Assert.False(preview.IsLoading);
        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public async Task Reload_AcceptsExactlyThePreviewLimit()
    {
        var path = Path.Combine(_root, "limit.txt");
        await File.WriteAllTextAsync(path, new string('a', TextDocumentViewModel.MaxFileBytes), new UTF8Encoding(false));
        using var preview = TextDocumentViewModel.ForFile(path, InlineUiScheduler.Instance);

        await preview.ReloadAsync();

        Assert.Equal(TextDocumentViewModel.MaxFileBytes, preview.Text.Length);
        Assert.Null(preview.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reload_RejectsBinaryIncludingNulAfterFirstReadBuffer(bool invalidUtf8)
    {
        var bytes = Encoding.UTF8.GetBytes(new string('a', 16384));
        bytes[^1] = invalidUtf8 ? (byte)0xff : (byte)0;
        var path = Path.Combine(_root, "binary.bin");
        await File.WriteAllBytesAsync(path, bytes);
        using var preview = TextDocumentViewModel.ForFile(path, InlineUiScheduler.Instance);

        await preview.ReloadAsync();

        Assert.Empty(preview.Text);
        Assert.NotNull(preview.Error);
        Assert.Contains("external application", preview.Error);
        Assert.False(preview.IsLoading);
    }

    [Fact]
    public async Task Reload_MissingFileAndIoFailureRemainVisibleAndCanRetry()
    {
        var path = Path.Combine(_root, "missing.txt");
        using var preview = TextDocumentViewModel.ForFile(path, InlineUiScheduler.Instance);
        await preview.ReloadAsync();
        Assert.Contains("Could not read", preview.Error);
        Assert.False(preview.IsLoading);

        await File.WriteAllTextAsync(path, "created after initial attempt");
        await preview.ReloadAsync();
        Assert.Equal("created after initial attempt", preview.Text);
        Assert.Null(preview.Error);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await preview.ReloadAsync();
            Assert.Contains("Could not read", preview.Error);
            Assert.Empty(preview.Text);
        }
        await preview.ReloadAsync();
        Assert.Null(preview.Error);
        Assert.Equal("created after initial attempt", preview.Text);
    }

    [Fact]
    public async Task Dispose_DiscardsQueuedAsyncCompletionAndPreventsReload()
    {
        var path = Path.Combine(_root, "closed.txt");
        await File.WriteAllTextAsync(path, "must not appear after close");
        var scheduler = new QueuedScheduler();
        var preview = TextDocumentViewModel.ForFile(path, scheduler);
        await preview.ReloadAsync();
        Assert.True(preview.IsLoading);
        Assert.Empty(preview.Text);

        preview.Dispose();
        scheduler.RunPending();
        await preview.ReloadAsync();

        Assert.Empty(preview.Text);
        Assert.Null(preview.Error);
        Assert.True(preview.IsDisposed);
        Assert.False(preview.CanReload);
        Assert.False(preview.ReloadCommand.CanExecute(null));
    }

    [Fact]
    public async Task Reload_SupersededCompletionCannotOverwriteLatestContent()
    {
        var path = Path.Combine(_root, "reload.txt");
        var scheduler = new QueuedScheduler();
        using var preview = TextDocumentViewModel.ForFile(path, scheduler);
        await File.WriteAllTextAsync(path, "old result");
        await preview.ReloadAsync();
        await File.WriteAllTextAsync(path, "new result");
        await preview.ReloadAsync();

        scheduler.RunPending();

        Assert.Equal("new result", preview.Text);
        Assert.Null(preview.Error);
        Assert.False(preview.IsLoading);
    }

    [Fact]
    public void GeneratedText_UsesSameReadOnlyContentAdapterWithWrapAndLanguage()
    {
        using var preview = new TextDocumentViewModel("Tool output", "first", "json");
        Assert.Null(preview.FilePath);
        Assert.Equal("json", preview.Language);
        Assert.False(preview.CanReload);
        Assert.Equal(TextWrapping.Wrap, preview.TextWrapping);
        Assert.Equal(ScrollBarVisibility.Disabled, preview.HorizontalScrollMode);

        preview.Wrap = false;
        preview.UpdateText("second");
        Assert.Equal(TextWrapping.NoWrap, preview.TextWrapping);
        Assert.Equal(ScrollBarVisibility.Auto, preview.HorizontalScrollMode);
        Assert.Equal("second", preview.Text);
        preview.Dispose();
        preview.UpdateText("ignored");
        Assert.Equal("second", preview.Text);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class QueuedScheduler : IUiScheduler
    {
        private readonly ConcurrentQueue<Action> _actions = new();

        public void Post(Action action) => _actions.Enqueue(action);

        public void RunPending()
        {
            while (_actions.TryDequeue(out var action))
            {
                action();
            }
        }
    }

    private sealed class EndlessTextStream : Stream
    {
        public int BytesRead { get; private set; }
        public bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, 137);
            buffer.Span[..count].Fill((byte)'a');
            BytesRead += count;
            return ValueTask.FromResult(count);
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
