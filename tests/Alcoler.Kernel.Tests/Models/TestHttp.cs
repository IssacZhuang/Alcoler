using System.Net;
using System.Text;

namespace Alcoler.Kernel.Tests.Models;

internal sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
    : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string?> RequestBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken));
        return responder(request, cancellationToken);
    }
}

internal sealed class SegmentedStream(byte[] data, int segmentSize) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var size = Math.Min(Math.Min(segmentSize, count), data.Length - _position);
        if (size <= 0)
            return 0;
        Array.Copy(data, _position, buffer, offset, size);
        _position += size;
        return size;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read(buffer, offset, count));
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public static HttpResponseMessage ToResponse(string body, int segmentSize = 7) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(new SegmentedStream(Encoding.UTF8.GetBytes(body), segmentSize)),
    };
}
