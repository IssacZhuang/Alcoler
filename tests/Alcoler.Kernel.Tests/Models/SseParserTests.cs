using System.Text;
using Alcoler.Adapters.Models;
using Xunit;

namespace Alcoler.Kernel.Tests.Models;

public class SseParserTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static List<SseFrame> FeedChunks(params string[] chunks)
    {
        var parser = new SseParser();
        var frames = new List<SseFrame>();
        foreach (var chunk in chunks)
            frames.AddRange(parser.Feed(Utf8(chunk)));
        frames.AddRange(parser.Flush());
        return frames;
    }

    [Fact]
    public void SimpleDataLine_YieldsFrame()
    {
        var frames = FeedChunks("data: hello\n\n");

        var frame = Assert.Single(frames);
        Assert.Equal("hello", frame.Data);
        Assert.Null(frame.Event);
    }

    [Fact]
    public void DataWithoutSpaceAfterColon_IsAccepted()
    {
        var frames = FeedChunks("data:hello\n\n");

        Assert.Equal("hello", Assert.Single(frames).Data);
    }

    [Fact]
    public void MultiDataLines_AreJoinedWithNewline()
    {
        var frames = FeedChunks("data: first\ndata: second\ndata: third\n\n");

        var frame = Assert.Single(frames);
        Assert.Equal("first\nsecond\nthird", frame.Data);
    }

    [Fact]
    public void EventField_IsAttachedAndReset()
    {
        var frames = FeedChunks("event: message\ndata: {\"a\":1}\n\ndata: {\"b\":2}\n\n");

        Assert.Equal(2, frames.Count);
        Assert.Equal("message", frames[0].Event);
        Assert.Null(frames[1].Event);
    }

    [Fact]
    public void CrlfLineEndings_AreHandled()
    {
        var frames = FeedChunks("event: ping\r\ndata: one\r\n\r\ndata: two\r\n\r\n");

        Assert.Equal(2, frames.Count);
        Assert.Equal("one", frames[0].Data);
        Assert.Equal("two", frames[1].Data);
    }

    [Fact]
    public void CommentLines_AreIgnored()
    {
        var frames = FeedChunks(": keep-alive comment\n:: double\ndata: payload\n\n");

        Assert.Equal("payload", Assert.Single(frames).Data);
    }

    [Fact]
    public void DoneSentinel_YieldsFrameWithDataDone()
    {
        var frames = FeedChunks("data: [DONE]\n\n");

        var frame = Assert.Single(frames);
        Assert.Equal("[DONE]", frame.Data);
    }

    [Fact]
    public void PartialLines_AcrossChunkSplits_AreReassembled()
    {
        var frames = FeedChunks("even", "t: mess", "age\nda", "ta: he", "llo\r", "\n\r", "\n");

        var frame = Assert.Single(frames);
        Assert.Equal("message", frame.Event);
        Assert.Equal("hello", frame.Data);
    }

    [Fact]
    public void Utf8Payload_SplitAcrossChunks_IsPreserved()
    {
        var parser = new SseParser();
        var bytes = Utf8("data: greet \u4f60\u597d\n\n");
        var frames = new List<SseFrame>();
        foreach (var b in bytes)
            frames.AddRange(parser.Feed([b]));

        Assert.Equal("greet \u4f60\u597d", Assert.Single(frames).Data);
    }

    [Fact]
    public void Flush_DispatchesTrailingFrameWithoutFinalBlankLine()
    {
        var parser = new SseParser();
        var frames = new List<SseFrame>(parser.Feed(Utf8("data: tail")));

        Assert.Empty(frames);
        frames.AddRange(parser.Flush());

        Assert.Equal("tail", Assert.Single(frames).Data);
    }

    [Fact]
    public void BlankLinesWithoutData_DoNotEmitFrames()
    {
        var frames = FeedChunks("\n\n\n: comment\n\n");

        Assert.Empty(frames);
    }

    [Fact]
    public void FeedLine_DispatchesCompleteLines()
    {
        var parser = new SseParser();
        Assert.Empty(parser.FeedLine("event: message"));
        Assert.Empty(parser.FeedLine("data: x"));
        var frames = new List<SseFrame>(parser.FeedLine(string.Empty));

        Assert.Equal("x", Assert.Single(frames).Data);
    }
}
