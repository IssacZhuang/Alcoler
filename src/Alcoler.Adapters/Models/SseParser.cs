using System.Text;

namespace Alcoler.Adapters.Models;

internal readonly record struct SseFrame(string? Event, string Data);

/// <summary>
/// Incremental server-sent-event parser. Bytes are fed in arbitrary chunk sizes;
/// a frame is dispatched on each blank line. Lines split across feeds and split
/// CR/LF pairs are buffered until complete. The <c>data: [DONE]</c> sentinel is
/// surfaced as a regular frame; callers decide its meaning.
/// </summary>
internal sealed class SseParser
{
    private readonly List<byte> _pending = [];
    private readonly List<string> _dataLines = [];
    private string? _event;

    public IReadOnlyList<SseFrame> Feed(ReadOnlySpan<byte> bytes)
    {
        var frames = new List<SseFrame>();
        var consumed = 0;
        while (consumed < bytes.Length)
        {
            var newline = bytes.Slice(consumed).IndexOf((byte)'\n');
            if (newline < 0)
            {
                _pending.AddRange(bytes.Slice(consumed).ToArray());
                break;
            }
            var lineEnd = consumed + newline;
            byte[] lineBytes;
            if (_pending.Count > 0)
            {
                _pending.AddRange(bytes.Slice(consumed, newline).ToArray());
                lineBytes = [.. _pending];
                _pending.Clear();
            }
            else
            {
                lineBytes = bytes.Slice(consumed, newline).ToArray();
            }
            FeedLineBytes(lineBytes, frames);
            consumed = lineEnd + 1;
        }
        return frames;
    }

    public IReadOnlyList<SseFrame> FeedText(ReadOnlySpan<char> text) =>
        Feed(Encoding.UTF8.GetBytes(text.ToArray()));

    public IReadOnlyList<SseFrame> FeedLine(string line)
    {
        var frames = new List<SseFrame>();
        FeedLineBytes(Encoding.UTF8.GetBytes(line), frames);
        return frames;
    }

    /// <summary>Dispatches a trailing frame whose last data line was not followed by a blank line.</summary>
    public IReadOnlyList<SseFrame> Flush()
    {
        var frames = new List<SseFrame>();
        if (_pending.Count > 0)
        {
            var lineBytes = _pending.ToArray();
            _pending.Clear();
            FeedLineBytes(lineBytes, frames);
        }
        Dispatch(frames);
        return frames;
    }

    private void FeedLineBytes(byte[] lineBytes, List<SseFrame> frames)
    {
        var length = lineBytes.Length;
        if (length > 0 && lineBytes[length - 1] == (byte)'\r')
            length--;

        var line = Encoding.UTF8.GetString(lineBytes, 0, length);
        if (line.Length == 0)
        {
            Dispatch(frames);
            return;
        }
        if (line[0] == ':')
            return;

        var colon = line.IndexOf(':');
        var field = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? "" : line[(colon + 1)..];
        if (value.StartsWith(' '))
            value = value[1..];

        switch (field)
        {
            case "data":
                _dataLines.Add(value);
                break;
            case "event":
                _event = value;
                break;
        }
    }

    private void Dispatch(List<SseFrame> frames)
    {
        if (_dataLines.Count == 0)
        {
            _event = null;
            return;
        }
        frames.Add(new SseFrame(_event, string.Join("\n", _dataLines)));
        _dataLines.Clear();
        _event = null;
    }
}
