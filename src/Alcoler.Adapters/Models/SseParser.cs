using System.Buffers;
using System.Text;

namespace Alcoler.Adapters.Models;

internal readonly record struct SseFrame(string? Event, string Data);

/// <summary>
/// Incremental server-sent-event parser. Bytes are fed in arbitrary chunk sizes;
/// a frame is dispatched on each blank line. Lines split across feeds and split
/// CR/LF pairs are buffered until complete. The <c>data: [DONE]</c> sentinel is
/// surfaced as a regular frame; callers decide its meaning.
/// </summary>
internal sealed class SseParser : IDisposable
{
    private readonly List<SseFrame> _frames = [];
    private readonly List<string> _dataLines = [];
    private byte[] _pending = [];
    private int _pendingCount;
    private bool _pendingRented;
    private string? _event;

    public IReadOnlyList<SseFrame> Feed(ReadOnlySpan<byte> bytes)
    {
        _frames.Clear();
        var consumed = 0;
        while (consumed < bytes.Length)
        {
            var newline = bytes[consumed..].IndexOf((byte)'\n');
            if (newline < 0)
            {
                AppendPending(bytes[consumed..]);
                break;
            }
            if (_pendingCount > 0)
            {
                AppendPending(bytes.Slice(consumed, newline));
                FeedLine(_pending.AsSpan(0, _pendingCount));
                _pendingCount = 0;
            }
            else
            {
                FeedLine(bytes.Slice(consumed, newline));
            }
            consumed += newline + 1;
        }
        return _frames;
    }

    public IReadOnlyList<SseFrame> FeedText(ReadOnlySpan<char> text)
    {
        var count = Encoding.UTF8.GetByteCount(text);
        if (count == 0)
            return Feed(ReadOnlySpan<byte>.Empty);
        var rented = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            var written = Encoding.UTF8.GetBytes(text, rented);
            return Feed(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public IReadOnlyList<SseFrame> FeedLine(string line)
    {
        _frames.Clear();
        var byteCount = Encoding.UTF8.GetByteCount(line);
        if (byteCount <= 512)
        {
            Span<byte> encoded = stackalloc byte[byteCount];
            Encoding.UTF8.GetBytes(line, encoded);
            FeedLine(encoded);
        }
        else
        {
            var rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                var written = Encoding.UTF8.GetBytes(line, rented);
                FeedLine(rented.AsSpan(0, written));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        return _frames;
    }

    /// <summary>Dispatches a trailing frame whose last data line was not followed by a blank line.</summary>
    public IReadOnlyList<SseFrame> Flush()
    {
        _frames.Clear();
        if (_pendingCount > 0)
        {
            var pending = _pending.AsSpan(0, _pendingCount);
            _pendingCount = 0;
            FeedLine(pending);
        }
        Dispatch();
        return _frames;
    }

    public void Dispose()
    {
        if (!_pendingRented)
            return;
        ArrayPool<byte>.Shared.Return(_pending);
        _pending = [];
        _pendingRented = false;
    }

    private void AppendPending(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;
        var required = _pendingCount + bytes.Length;
        if (required > _pending.Length)
        {
            var size = Math.Max(required, Math.Max(256, _pending.Length * 2));
            var grown = ArrayPool<byte>.Shared.Rent(size);
            if (_pendingCount > 0)
                _pending.AsSpan(0, _pendingCount).CopyTo(grown);
            if (_pendingRented)
                ArrayPool<byte>.Shared.Return(_pending);
            _pending = grown;
            _pendingRented = true;
        }
        bytes.CopyTo(_pending.AsSpan(_pendingCount));
        _pendingCount = required;
    }

    private void FeedLine(ReadOnlySpan<byte> line)
    {
        if (!line.IsEmpty && line[^1] == (byte)'\r')
            line = line[..^1];
        if (line.IsEmpty)
        {
            Dispatch();
            return;
        }
        if (line[0] == (byte)':')
            return;

        var colon = line.IndexOf((byte)':');
        var field = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? default : line[(colon + 1)..];
        if (!value.IsEmpty && value[0] == (byte)' ')
            value = value[1..];

        if (field.SequenceEqual("data"u8))
            _dataLines.Add(Encoding.UTF8.GetString(value));
        else if (field.SequenceEqual("event"u8))
            _event = Encoding.UTF8.GetString(value);
    }

    private void Dispatch()
    {
        if (_dataLines.Count == 0)
        {
            _event = null;
            return;
        }
        _frames.Add(new SseFrame(_event,
            _dataLines.Count == 1 ? _dataLines[0] : string.Join("\n", _dataLines)));
        _dataLines.Clear();
        _event = null;
    }
}
