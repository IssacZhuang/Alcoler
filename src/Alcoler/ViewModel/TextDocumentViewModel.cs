using System.Text;
using Alcoler.Ui;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Alcoler.ViewModel;

public sealed partial class TextDocumentViewModel : ObservableObject, IDisposable
{
    public const int MaxFileBytes = 256 * 1024;

    private readonly IUiScheduler _scheduler;
    private readonly Func<CancellationToken, Task<Stream>>? _openStream;
    private CancellationTokenSource? _cancellation;
    private long _loadVersion;
    private bool _disposed;

    public TextDocumentViewModel(string sourceLabel, string text, string? language = null)
    {
        SourceLabel = sourceLabel;
        _text = text;
        Language = string.IsNullOrWhiteSpace(language) ? "Plain text" : language;
        _scheduler = InlineUiScheduler.Instance;
    }

    internal TextDocumentViewModel(
        string filePath,
        IUiScheduler scheduler,
        Func<CancellationToken, Task<Stream>> openStream)
    {
        FilePath = filePath;
        SourceLabel = filePath;
        Language = Path.GetExtension(filePath).TrimStart('.') is { Length: > 0 } extension ? extension : "Plain text";
        _scheduler = scheduler;
        _openStream = openStream;
    }

    public static TextDocumentViewModel ForFile(string path, IUiScheduler scheduler) => new(path, scheduler,
        _ => Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan)));

    public string? FilePath { get; }

    public string SourceLabel { get; }

    public string Language { get; }

    public bool CanReload => FilePath is not null && !_disposed;

    public bool IsDisposed => _disposed;

    public Task LoadingTask { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextWrapping))]
    [NotifyPropertyChangedFor(nameof(HorizontalScrollMode))]
    private bool _wrap = true;

    public TextWrapping TextWrapping => Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;

    public ScrollBarVisibility HorizontalScrollMode => Wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

    [RelayCommand(CanExecute = nameof(CanReload))]
    public Task ReloadAsync()
    {
        if (!CanReload)
        {
            return Task.CompletedTask;
        }
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        var version = ++_loadVersion;
        IsLoading = true;
        Error = null;
        Text = string.Empty;
        LoadingTask = ReadAndApplyAsync(version, _cancellation.Token);
        return LoadingTask;
    }

    public void UpdateText(string text)
    {
        if (!_disposed && FilePath is null)
        {
            Text = text;
        }
    }

    private async Task ReadAndApplyAsync(long version, CancellationToken cancellationToken)
    {
        PreviewResult result;
        try
        {
            result = await Task.Run(() => ReadPreviewAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            result = new PreviewResult(string.Empty, $"Could not read this file. Check that it exists and is readable, then reload. {exception.Message}");
        }
        _scheduler.Post(() =>
        {
            if (_disposed || version != _loadVersion || cancellationToken.IsCancellationRequested)
            {
                return;
            }
            Text = result.Text;
            Error = result.Error;
            IsLoading = false;
        });
    }

    private async Task<PreviewResult> ReadPreviewAsync(CancellationToken cancellationToken)
    {
        await using var stream = await _openStream!(cancellationToken).ConfigureAwait(false);
        var bytes = new byte[MaxFileBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            length += read;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (length > MaxFileBytes)
        {
            return new PreviewResult(string.Empty, $"This file exceeds the {MaxFileBytes / 1024} KiB preview limit. Open a smaller file or use an external viewer.");
        }
        try
        {
            var text = Decode(bytes.AsSpan(0, length));
            return text.Contains('\0')
                ? new PreviewResult(string.Empty, "Binary files cannot be previewed as text. Open this file in an external application.")
                : new PreviewResult(text, null);
        }
        catch (DecoderFallbackException)
        {
            return new PreviewResult(string.Empty,
                "This file uses binary data or an unsupported encoding. Use UTF-8 or BOM-marked UTF-16/UTF-32 text, or open it in an external application.");
        }
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        Encoding encoding = new UTF8Encoding(false, true);
        var preamble = 0;
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe, 0x00, 0x00 }))
        {
            encoding = new UTF32Encoding(false, true, true);
            preamble = 4;
        }
        else if (bytes.StartsWith(new byte[] { 0x00, 0x00, 0xfe, 0xff }))
        {
            encoding = new UTF32Encoding(true, true, true);
            preamble = 4;
        }
        else if (bytes.StartsWith(new byte[] { 0xff, 0xfe }))
        {
            encoding = new UnicodeEncoding(false, true, true);
            preamble = 2;
        }
        else if (bytes.StartsWith(new byte[] { 0xfe, 0xff }))
        {
            encoding = new UnicodeEncoding(true, true, true);
            preamble = 2;
        }
        else if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            preamble = 3;
        }
        return encoding.GetString(bytes[preamble..]);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ++_loadVersion;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        ReloadCommand.NotifyCanExecuteChanged();
    }

    private sealed record PreviewResult(string Text, string? Error);
}
