using System.Buffers;
using System.Reflection;
using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Context;
using Xunit;

namespace Alcoler.Kernel.Tests.Context;

public class TokenEstimatorTests
{
    [Fact]
    public void EstimateTokens_PlainAscii_CeilsDividedByThree()
    {
        Assert.Equal(0, TokenEstimator.EstimateTokens(""));
        Assert.Equal(1, TokenEstimator.EstimateTokens("ab"));
        Assert.Equal(1, TokenEstimator.EstimateTokens("abc"));
        Assert.Equal(2, TokenEstimator.EstimateTokens("abcd"));
        Assert.Equal(3, TokenEstimator.EstimateTokens("abcdefghi"));
    }

    [Fact]
    public void EstimateTokens_CjkCountsDouble()
    {
        Assert.Equal(1, TokenEstimator.EstimateTokens("中"));
        Assert.Equal(2, TokenEstimator.EstimateTokens("中文"));
        Assert.Equal(2, TokenEstimator.EstimateTokens("ab中"));
        Assert.Equal(1, TokenEstimator.EstimateTokens("\u4DBF\u4E00"));
    }

    [Fact]
    public void EstimateTokens_OnlyUnifiedCjkGetsExtraWeightWhileAstralUsesUtf16Length()
    {
        Assert.Equal(1, TokenEstimator.EstimateTokens("\u3400\u3400\u4DBF"));
        Assert.Equal(1, TokenEstimator.EstimateTokens("\uFF21\uFF21\uFFEF"));
        Assert.Equal(2, TokenEstimator.EstimateTokens("\u4E00\u4E00\u9FFF"));
        Assert.Equal(1, TokenEstimator.EstimateTokens("\uFF21"));
        Assert.Equal(1, TokenEstimator.EstimateTokens("\U00020000"));
        Assert.Equal(1, TokenEstimator.EstimateTokens("a\U0002A700"));
        Assert.Equal(2, TokenEstimator.EstimateTokens("aa\U0002A700"));
    }

    [Fact]
    public void EstimateMessageTokens_CountsEveryPartKind()
    {
        var callId = StrongId.NewToolCallId();
        var message = new ConversationMessage(
            StrongId.NewMessageId(),
            MessageRole.User,
        [
            new TextPart(StrongId.NewPartId(), "abc"),
                new ReasoningPart(StrongId.NewPartId(), "reasoning text"),
                new ToolCallPart(StrongId.NewPartId(), callId, "Read", TestJson.Element(new { path = "src/Program.cs" })),
                new ToolResultPart(StrongId.NewPartId(), callId, [new ToolResultText("0123456789")]),
                new FilePart(StrongId.NewPartId(), "alcoler-artifact://s/a.png", "image/png", "a.png"),
                new AttachmentPart(StrongId.NewPartId(), AttachmentKind.SystemReminder, "reminder content"),
        ],
            DateTimeOffset.UtcNow);

        var content = string.Join("\n\n", "abc", "reasoning text", "0123456789", "[media file: a.png]", "reminder content");
        var characters = content.Length + "Read".Length + TestJson.Element(new { path = "src/Program.cs" }).GetRawText().Length;
        var expected = (characters + 2) / 3;

        Assert.Equal(expected, TokenEstimator.EstimateMessageTokens(message));
    }

    [Fact]
    public void EstimateMessageTokens_ToolCallInputJsonIsCounted()
    {
        var callId = StrongId.NewToolCallId();
        var longInput = TestJson.Element(new { path = new string('p', 300) });
        var withCall = new ConversationMessage(
            StrongId.NewMessageId(),
            MessageRole.Assistant,
            [new ToolCallPart(StrongId.NewPartId(), callId, "Read", longInput)],
            DateTimeOffset.UtcNow);
        var withoutCall = new ConversationMessage(
            StrongId.NewMessageId(),
            MessageRole.Assistant,
            [new TextPart(StrongId.NewPartId(), "x")],
            DateTimeOffset.UtcNow);

        var callTokens = TokenEstimator.EstimateMessageTokens(withCall);
        Assert.Equal(("Read".Length + longInput.GetRawText().Length + 2) / 3, callTokens);
        Assert.True(callTokens > TokenEstimator.EstimateMessageTokens(withoutCall));
    }

    [Fact]
    public void EstimateHistoryTokens_SumsMessages()
    {
        var history = new List<ConversationMessage>
        {
            TestMessages.UserText("hello world"),
            TestMessages.AssistantText("hi there"),
        };
        var expected = TokenEstimator.EstimateTokens("hello world") + TokenEstimator.EstimateTokens("hi there");

        Assert.Equal(expected, TokenEstimator.EstimateHistoryTokens(history));
        Assert.Equal(0, TokenEstimator.EstimateHistoryTokens([]));
    }

    [Fact]
    public void EstimateCurrentInputTokens_UsesProviderAnchorPlusLocalTail()
    {
        var history = new List<ConversationMessage>
        {
            TestMessages.UserText("one"),
            TestMessages.AssistantText("two"),
            TestMessages.AssistantText("three"),
            TestMessages.UserText("four"),
        };
        var usage = new TokenUsage(ContextUsageTokens: 1_000);

        var (tokens, source) = TokenEstimator.EstimateCurrentInputTokens(history, anchorIndex: 1, usage);

        Assert.Equal("provider_usage", source);
        Assert.Equal(1_000 + TokenEstimator.EstimateHistoryTokens(history.Skip(2).ToList()), tokens);
    }

    [Fact]
    public void EstimateCurrentInputTokens_FallsBackToInputTokens()
    {
        var history = new List<ConversationMessage> { TestMessages.UserText("one"), TestMessages.AssistantText("two") };
        var usage = new TokenUsage(InputTokens: 500);

        var (tokens, source) = TokenEstimator.EstimateCurrentInputTokens(history, anchorIndex: 1, usage);

        Assert.Equal("provider_usage", source);
        Assert.Equal(500 + TokenEstimator.EstimateMessageTokens(history[1]), tokens);
    }

    [Fact]
    public void EstimateModelMessageTokens_UsesUnweightedJoinedContentAndCountsReasoning()
    {
        var message = new ModelInputMessage(MessageRole.Assistant,
            [new InputTextBlock("中文中"), new InputReasoningBlock("abcdef"), new InputTextBlock("")]);

        Assert.Equal((3 + 2 + 6 + 2) / 3, TokenEstimator.EstimateModelMessageTokens(message));
        Assert.Equal(1, TokenEstimator.EstimateMessageTokens(TestMessages.UserText("中文中")));
        Assert.Equal(2, TokenEstimator.EstimateTokens("中文中"));
    }

    [Fact]
    public void EstimateMessageTokens_CountsToolNameAndNormalizedInputAndOmitsHiddenAttachments()
    {
        var input = JsonDocument.Parse("{ \"value\" : \"abc\" }").RootElement.Clone();
        var message = TestMessages.AssistantText("") with
        {
            Parts = [new ToolCallPart(StrongId.NewPartId(), StrongId.NewToolCallId(), "ToolName", input)],
        };

        Assert.Equal(("ToolName{\"value\":\"abc\"}".Length + 2) / 3, TokenEstimator.EstimateMessageTokens(message));
        Assert.Equal(0, TokenEstimator.EstimateMessageTokens(TestMessages.UserAttachment(AttachmentKind.SystemReminder, "hidden", providerVisible: false)));
        Assert.Equal(("ToolName{}".Length + 2) / 3, TokenEstimator.EstimateModelMessageTokens(
            new ModelInputMessage(MessageRole.Assistant,
                [new InputToolCallBlock(StrongId.NewToolCallId(), "ToolName", default)])));
    }

    [Fact]
    public void EstimateRequestTokens_KeepsMarkdownAndNonAsciiLiteral()
    {
        var tool = new ToolContract("Describe", "don't use `x` — 中文", TestJson.Element(new { type = "object" }));
        var request = new ModelRequest([], [tool], new ModelCallOptions());

        var expected = TokenEstimator.EstimateTokens(
            "{\"name\":\"Describe\",\"description\":\"don't use `x` — 中文\",\"inputSchema\":{\"type\":\"object\"}}");

        Assert.Equal(expected, TokenEstimator.EstimateRequestTokens(request));
    }

    [Fact]
    public void EstimateModelMessageTokens_ImageCountsPlaceholderNotBase64Payload()
    {
        var message = new ModelInputMessage(MessageRole.User, [new InputImageBlock("image/png", new string('a', 50_000))]);

        Assert.Equal(("[Attached image/png]".Length + 2) / 3, TokenEstimator.EstimateModelMessageTokens(message));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EstimateRequestTokens_ImagesDoNotCountBase64Payload(bool inline)
    {
        ModelRequest Request(string data)
        {
            var image = new InputImageBlock("image/png", data);
            ModelContentBlock block = inline
                ? new InputToolResultBlock(new ToolCallId("image-call"), "image", false, Images: [image])
                : image;
            return new ModelRequest([new ModelInputMessage(MessageRole.User, [block])], [], new ModelCallOptions());
        }

        var small = TokenEstimator.EstimateRequestTokens(Request("a"));
        var large = TokenEstimator.EstimateRequestTokens(Request(new string('a', 4_000_000)));

        Assert.Equal(small, large);
        Assert.InRange(large, 1, 1_000);
        if (inline)
        {
            var withoutImage = new ModelRequest(
                [new ModelInputMessage(MessageRole.User,
                    [new InputToolResultBlock(new ToolCallId("image-call"), "image", false)])], [], new ModelCallOptions());
            Assert.True(large > TokenEstimator.EstimateRequestTokens(withoutImage));
        }
    }

    [Fact]
    public void JsonLengthMeter_MatchesSerializedReferencesAcrossGrowthAndReset()
    {
        const string escaped = "中\u3400\u4DBF\u4E00\u9FFF\uFF21\U00020000\U0001F600\"\\\n\r\t\u0001<>`'&—\uD800x\uDC00";
        var input = TestJson.Element(new { text = escaped, values = new object?[] { null, true, 1.5 } });
        var callId = new ToolCallId("call\"中文");
        using var meter = new TokenEstimator.JsonLengthMeter();
        foreach (var length in new[] { 0, 1, 255, 4_096, 65_537, 1_100_000, 3 })
        {
            var text = new string('a', length) + escaped;
            var message = new ModelInputMessage(MessageRole.Assistant,
            [
                new InputTextBlock(text),
                new InputReasoningBlock(escaped),
                new InputImageBlock("image/png", "not-counted"),
                new InputToolCallBlock(callId, "Inspect中文", input),
                new InputToolResultBlock(callId, escaped, true, [new InputImageBlock("image/jpeg", "not-counted")]),
            ]);
            var tool = new ToolContract("Inspect中文", text, input);
            var messageJson = TokenEstimator.SerializeMessage(message);
            var toolJson = TokenEstimator.SerializeTool(tool);

            Assert.Equal((messageJson.Length, TokenEstimator.EstimateTokens(messageJson)), meter.MeasureMessage(message));
            Assert.Equal((toolJson.Length, TokenEstimator.EstimateTokens(toolJson)), meter.MeasureTool(tool));
        }
        var undefinedSchema = new ToolContract("NoSchema", "", default);
        var undefinedJson = TokenEstimator.SerializeTool(undefinedSchema);
        Assert.Equal((undefinedJson.Length, TokenEstimator.EstimateTokens(undefinedJson)), meter.MeasureTool(undefinedSchema));
    }

    [Fact]
    public void EstimateRequestTokens_PreservesPerMessageAndToolRounding()
    {
        var messages = Enumerable.Range(0, 3).Select(length =>
            new ModelInputMessage(MessageRole.User, [new InputTextBlock(new string('x', length))])).ToArray();
        var tools = Enumerable.Range(0, 3).Select(length => new ToolContract("Tool", new string('x', length), default)).ToArray();
        var serialized = messages.Select(TokenEstimator.SerializeMessage).Concat(tools.Select(TokenEstimator.SerializeTool)).ToArray();
        var expected = serialized.Sum(TokenEstimator.EstimateTokens);

        Assert.NotEqual(expected, TokenEstimator.EstimateTokens(string.Concat(serialized)));
        Assert.Equal(expected, TokenEstimator.EstimateRequestTokens(new ModelRequest(messages, tools, new ModelCallOptions())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JsonLengthMeter_DisposeReleasesLeaseAndIsIdempotent(bool measured)
    {
        using var meter = new TokenEstimator.JsonLengthMeter();
        var message = new ModelInputMessage(MessageRole.User, [new InputTextBlock("a message")]);
        if (measured)
        {
            meter.MeasureMessage(message);
            Assert.NotEmpty(MeterBytes(meter));
        }
        meter.Dispose();
        meter.Dispose();

        Assert.Empty(MeterBytes(meter));
        Assert.Throws<ObjectDisposedException>(() => meter.MeasureMessage(message));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void JsonLengthMeter_SerializationErrorsReleaseLeaseAndAllowReset(bool tool, bool recover)
    {
        using var document = JsonDocument.Parse("{}");
        var disposedInput = document.RootElement;
        document.Dispose();
        using var meter = new TokenEstimator.JsonLengthMeter();
        var text = new string('x', 16_384);
        if (tool)
        {
            Assert.Throws<ObjectDisposedException>(() => meter.MeasureTool(new ToolContract("Tool", text, disposedInput)));
        }
        else
        {
            var invalid = new ModelInputMessage(MessageRole.Assistant,
                [new InputTextBlock(text), new InputToolCallBlock(new ToolCallId("call"), "Tool", disposedInput)]);
            Assert.Throws<ObjectDisposedException>(() => meter.MeasureMessage(invalid));
        }
        Assert.NotEmpty(MeterBytes(meter));
        if (recover)
        {
            var message = new ModelInputMessage(MessageRole.User, [new InputTextBlock("中文\"\\\n")]);
            var json = TokenEstimator.SerializeMessage(message);
            Assert.Equal((json.Length, TokenEstimator.EstimateTokens(json)), meter.MeasureMessage(message));
        }
        meter.Dispose();

        Assert.Empty(MeterBytes(meter));
    }

    [Fact]
    public void JsonLengthMeter_ConcurrentMetersHaveIndependentLeases()
    {
        Parallel.For(0, 16, index =>
        {
            var message = new ModelInputMessage(MessageRole.User, [new InputTextBlock(new string('中', 4_096 + index))]);
            var tool = new ToolContract("Tool", new string('x', 8_192 + index), default);
            var messageJson = TokenEstimator.SerializeMessage(message);
            var toolJson = TokenEstimator.SerializeTool(tool);
            using var first = new TokenEstimator.JsonLengthMeter();
            using var second = new TokenEstimator.JsonLengthMeter();
            for (var iteration = 0; iteration < 16; iteration++)
            {
                Assert.Equal((messageJson.Length, TokenEstimator.EstimateTokens(messageJson)), first.MeasureMessage(message));
                Assert.Equal((toolJson.Length, TokenEstimator.EstimateTokens(toolJson)), second.MeasureTool(tool));
                Assert.NotSame(MeterBytes(first), MeterBytes(second));
            }
        });
    }

    [Fact]
    public void JsonLengthMeter_LargeOutliersAreNotRetained()
    {
        const int maxPooledLength = 4 << 20;
        var large = new ModelInputMessage(MessageRole.User, [new InputTextBlock(new string('x', maxPooledLength / 2))]);
        byte[] firstOutlier;
        using (var first = new TokenEstimator.JsonLengthMeter())
        {
            first.MeasureMessage(large);
            firstOutlier = MeterBytes(first);
            Assert.True(firstOutlier.Length > maxPooledLength);
            first.Dispose();
            Assert.Empty(MeterBytes(first));
        }
        using (var second = new TokenEstimator.JsonLengthMeter())
        {
            second.MeasureMessage(large);
            Assert.NotSame(firstOutlier, MeterBytes(second));
        }
        using var small = new TokenEstimator.JsonLengthMeter();
        small.MeasureMessage(new ModelInputMessage(MessageRole.User, [new InputTextBlock("small")]));
        Assert.InRange(MeterBytes(small).Length, 1, maxPooledLength);
    }

    [Fact]
    public void JsonLengthMeter_PoolRetainsOneArrayPerBucketUpToFourMiB()
    {
        const int maxPooledLength = 4 << 20;
        var bufferType = typeof(TokenEstimator.JsonLengthMeter).GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType;
        var pool = (ArrayPool<byte>)bufferType.GetMethod("CreatePool", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        Assert.NotSame(ArrayPool<byte>.Shared, pool);
        var lengths = new List<int>();
        for (var length = 16; length <= maxPooledLength; length *= 2)
            lengths.Add(length);
        // Every bucket must be leased before duplicate rents can bypass adjacent-bucket fallback.
        var first = lengths.Select(pool.Rent).ToArray();
        var second = lengths.Select(pool.Rent).ToArray();
        foreach (var buffer in first.Concat(second))
            pool.Return(buffer);
        var reused = lengths.Select(pool.Rent).ToArray();
        var replaced = lengths.Select(pool.Rent).ToArray();
        long retainedBytes = 0;
        for (var index = 0; index < lengths.Count; index++)
        {
            Assert.Equal(lengths[index], first[index].Length);
            Assert.Equal(lengths[index], second[index].Length);
            Assert.Same(first[index], reused[index]);
            Assert.NotSame(second[index], replaced[index]);
            retainedBytes += reused[index].Length;
        }
        foreach (var buffer in reused.Concat(replaced))
            pool.Return(buffer);
        Assert.Equal(8L * 1024 * 1024 - 16, retainedBytes);
        var outlier = pool.Rent(maxPooledLength + 1);
        pool.Return(outlier);
        var nextOutlier = pool.Rent(maxPooledLength + 1);
        Assert.NotSame(outlier, nextOutlier);
        pool.Return(nextOutlier);
    }

    private static byte[] MeterBytes(TokenEstimator.JsonLengthMeter meter)
    {
        var buffer = typeof(TokenEstimator.JsonLengthMeter).GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(meter)!;
        return (byte[])buffer.GetType().GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(buffer)!;
    }

    [Fact]
    public void EstimateCurrentInputTokens_WithoutUsage_EstimatesWholeHistory()
    {
        var history = new List<ConversationMessage> { TestMessages.UserText("one"), TestMessages.AssistantText("two") };

        var (tokens, source) = TokenEstimator.EstimateCurrentInputTokens(history, anchorIndex: 1, usage: null);

        Assert.Equal("estimate", source);
        Assert.Equal(TokenEstimator.EstimateHistoryTokens(history), tokens);
    }
}

internal static class TestJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    internal static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Options);
}
