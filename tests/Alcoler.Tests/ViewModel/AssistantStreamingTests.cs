using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.ViewModel;
using Xunit;

namespace Alcoler.Tests.ViewModel;

public class AssistantStreamingTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Streaming_CommitsStableBlocksAndKeepsTail()
    {
        var viewModel = new AssistantMessageItemViewModel("a1", StrongId.NewMessageId());
        var text = "# Heading\n\nFirst paragraph.\n\nSecond";

        viewModel.UpdateFrom(Row(text, streaming: true));

        Assert.Equal(2, viewModel.StreamingChunks.Count);
        Assert.Equal("# Heading\n\n", viewModel.StreamingChunks[0].Text);
        Assert.Equal("First paragraph.\n\n", viewModel.StreamingChunks[1].Text);
        Assert.Equal("Second", viewModel.StreamingTail);
        Assert.True(viewModel.HasStreamingTail);
        Assert.True(viewModel.IsStreaming);
        Assert.Empty(viewModel.FinalMarkdown);
        Assert.Equal(text, string.Concat(viewModel.StreamingChunks.Select(chunk => chunk.Text)) + viewModel.StreamingTail);
    }

    [Fact]
    public void Completion_ReplacesStreamingViewWithFinalMarkdown()
    {
        var viewModel = new AssistantMessageItemViewModel("a1", StrongId.NewMessageId());
        var text = "Done first.\n\nDone second.";

        viewModel.UpdateFrom(Row(text, streaming: true));
        viewModel.UpdateFrom(Row(text, streaming: false));

        Assert.False(viewModel.IsStreaming);
        Assert.Empty(viewModel.StreamingChunks);
        Assert.Empty(viewModel.StreamingTail);
        Assert.False(viewModel.HasStreamingTail);
        Assert.Equal(text, viewModel.FinalMarkdown);
        Assert.True(viewModel.HasFinalMarkdown);
        Assert.Equal(text, viewModel.MarkdownText);
    }

    [Fact]
    public void FencedCode_IsCommittedOnlyAfterClosingFence()
    {
        var viewModel = new AssistantMessageItemViewModel("a1", StrongId.NewMessageId());
        var text = "Intro\n\n```csharp\nvar x = 1;\n\nvar y = 2;\n";

        viewModel.UpdateFrom(Row(text, streaming: true));

        Assert.Single(viewModel.StreamingChunks);
        Assert.Equal("Intro\n\n", viewModel.StreamingChunks[0].Text);
        Assert.Equal("```csharp\nvar x = 1;\n\nvar y = 2;\n", viewModel.StreamingTail);
        Assert.True(viewModel.StreamingTailIsCode);

        viewModel.UpdateFrom(Row(text + "```\n", streaming: true));

        Assert.Equal(2, viewModel.StreamingChunks.Count);
        Assert.Equal("```csharp\nvar x = 1;\n\nvar y = 2;\n```\n", viewModel.StreamingChunks[1].Text);
        Assert.Empty(viewModel.StreamingTail);
        Assert.False(viewModel.StreamingTailIsCode);
    }

    [Fact]
    public void List_IsNotSplitAcrossBlankLines()
    {
        var viewModel = new AssistantMessageItemViewModel("a1", StrongId.NewMessageId());
        var text = "Intro\n\n- one\n\n- two\n\nTail";

        viewModel.UpdateFrom(Row(text, streaming: true));

        Assert.Single(viewModel.StreamingChunks);
        Assert.Equal("Intro\n\n- one\n\n- two\n\n", viewModel.StreamingChunks[0].Text);
        Assert.Equal("Tail", viewModel.StreamingTail);
    }

    [Fact]
    public void ReplacedText_RebuildsStreamingChunks()
    {
        var viewModel = new AssistantMessageItemViewModel("a1", StrongId.NewMessageId());
        viewModel.UpdateFrom(Row("One\n\nTwo", streaming: true));
        Assert.Single(viewModel.StreamingChunks);

        viewModel.UpdateFrom(Row("Completely different", streaming: true));

        Assert.Empty(viewModel.StreamingChunks);
        Assert.Equal("Completely different", viewModel.StreamingTail);
    }

    [Fact]
    public void SingleTextPart_ReusesSourceForStreamingAndCompletedText()
    {
        var text = new string('x', 100_000);
        var row = Row(text, streaming: true);
        var viewModel = new AssistantMessageItemViewModel(row.RowId, row.Id);

        viewModel.UpdateFrom(row);

        Assert.Same(text, viewModel.MarkdownText);
        Assert.True(viewModel.HasText);
        viewModel.UpdateFrom(row with { IsStreaming = false, CanFork = true });
        Assert.Same(text, viewModel.MarkdownText);
        Assert.Same(text, viewModel.FinalMarkdown);
        Assert.True(viewModel.CanFork);
    }

    [Fact]
    public void MultipleTextParts_PreserveEmptyPartsSeparatorsAndReasoning()
    {
        var row = Row(string.Empty, streaming: false) with
        {
            Parts =
            [
                new TextPart(StrongId.NewPartId(), string.Empty),
                new ReasoningPart(StrongId.NewPartId(), "reasoning"),
                new FilePart(StrongId.NewPartId(), "artifact://image", "image/png"),
                new TextPart(StrongId.NewPartId(), "middle"),
                new TextPart(StrongId.NewPartId(), string.Empty),
            ],
        };
        var viewModel = new AssistantMessageItemViewModel(row.RowId, row.Id);

        viewModel.UpdateFrom(row);
        var text = viewModel.MarkdownText;
        viewModel.UpdateFrom(row with
        {
            Parts = row.Parts.ToArray(),
            CanFork = true,
        });

        Assert.Equal("\n\nmiddle\n\n", text);
        Assert.Same(text, viewModel.MarkdownText);
        Assert.Same(text, viewModel.FinalMarkdown);
        Assert.Equal("reasoning", Assert.Single(viewModel.ReasoningBlocks));
    }

    [Fact]
    public void ReasoningOnlyUpdates_DoNotRebuildUnchangedMultipartText()
    {
        var first = new TextPart(StrongId.NewPartId(), new string('a', 50_000));
        var second = new TextPart(StrongId.NewPartId(), new string('b', 50_000));
        var row = Row(string.Empty, streaming: false) with { Parts = [first, second] };
        var viewModel = new AssistantMessageItemViewModel(row.RowId, row.Id);
        viewModel.UpdateFrom(row);
        var text = viewModel.MarkdownText;
        var updates = Enumerable.Range(0, 1000).Select(index => row with
        {
            Parts = [first, new ReasoningPart(StrongId.NewPartId(), $"reasoning {index}"), second],
            CanFork = index % 2 == 0,
        }).ToArray();

        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var update in updates)
            viewModel.UpdateFrom(update);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"1,000 reasoning-only updates: {allocated:N0} bytes allocated.");
        Assert.Same(text, viewModel.MarkdownText);
        Assert.Same(text, viewModel.FinalMarkdown);
        Assert.Equal("reasoning 999", Assert.Single(viewModel.ReasoningBlocks));
        Assert.True(allocated < 512_000, $"Unchanged text updates allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void AppendText_PreservesExactTextMetadataAndStableChunks()
    {
        var row = Row("\n\nIntro\r\n\r\nTail", streaming: true) with
        {
            Parts =
            [
                new ReasoningPart(StrongId.NewPartId(), "reasoning"),
                new TextPart(StrongId.NewPartId(), "\n\nIntro\r\n\r\nTail"),
            ],
        };
        var viewModel = new AssistantMessageItemViewModel(row.RowId, row.Id);
        viewModel.UpdateFrom(row);
        var chunk = Assert.Single(viewModel.StreamingChunks);
        var at = row.At.AddSeconds(1);

        viewModel.AppendText(new RowTextAppended(row.RowId, " continues\n\n```csharp\nvar x = 1;\n", at));
        viewModel.AppendText(new RowTextAppended(row.RowId, "```\n\nAfter", at.AddSeconds(1)));

        var expected = "\n\nIntro\r\n\r\nTail continues\n\n```csharp\nvar x = 1;\n```\n\nAfter";
        Assert.Equal(expected, viewModel.MarkdownText);
        Assert.Same(chunk, viewModel.StreamingChunks[0]);
        Assert.Equal("After", viewModel.StreamingTail);
        Assert.False(viewModel.StreamingTailIsCode);
        Assert.True(viewModel.IsStreaming);
        Assert.False(viewModel.CanFork);
        Assert.Equal("reasoning", Assert.Single(viewModel.ReasoningBlocks));
        Assert.Equal(at.AddSeconds(1), viewModel.At);

        viewModel.UpdateFrom(row with
        {
            Parts = [new TextPart(StrongId.NewPartId(), "Corrected final answer")],
            IsStreaming = false,
            CanFork = true,
        });

        Assert.Equal("Corrected final answer", viewModel.MarkdownText);
        Assert.Equal(viewModel.MarkdownText, viewModel.FinalMarkdown);
        Assert.Empty(viewModel.StreamingChunks);
        Assert.Empty(viewModel.StreamingTail);
        Assert.Empty(viewModel.ReasoningBlocks);
        Assert.True(viewModel.CanFork);
    }

    [Fact]
    public void EmptyAppend_OnlyAdvancesTimestampAndFullReplacementCanClearText()
    {
        var row = Row(string.Empty, streaming: true);
        var viewModel = new AssistantMessageItemViewModel(row.RowId, row.Id);
        viewModel.UpdateFrom(row);
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        viewModel.AppendText(new RowTextAppended(row.RowId, string.Empty, row.At.AddSeconds(1)));

        Assert.Equal(row.At.AddSeconds(1), viewModel.At);
        Assert.False(viewModel.HasText);
        Assert.Empty(changes);
        viewModel.AppendText(new RowTextAppended(row.RowId, "answer", row.At.AddSeconds(2)));
        Assert.True(viewModel.HasText);
        Assert.Contains(nameof(AssistantMessageItemViewModel.HasText), changes);
        Assert.Contains(nameof(AssistantMessageItemViewModel.MarkdownText), changes);
        Assert.Equal("answer", viewModel.MarkdownText);
        viewModel.UpdateFrom(row with { IsStreaming = false });
        Assert.False(viewModel.HasText);
        Assert.Empty(viewModel.MarkdownText);
        Assert.Empty(viewModel.FinalMarkdown);
    }

    [Fact]
    public void RestartingStreamingWithUnchangedText_RebuildsStreamingPresentation()
    {
        var row = Row("Intro\n\nTail", streaming: false);
        var viewModel = new AssistantMessageItemViewModel(row.RowId, row.Id);
        viewModel.UpdateFrom(row);

        viewModel.UpdateFrom(row with { IsStreaming = true });

        Assert.Single(viewModel.StreamingChunks);
        Assert.Equal("Tail", viewModel.StreamingTail);
        Assert.Empty(viewModel.FinalMarkdown);
        Assert.True(viewModel.HasText);
    }

    [Fact]
    public void AppendText_LongFineGrainedStreamDoesNotMaterializeFullPrefixes()
    {
        const int count = 4096;
        const string delta = "Paragraph.\n\nNext\n\n";
        var row = Row(string.Empty, streaming: true);
        var viewModel = new AssistantMessageItemViewModel(row.RowId, row.Id);
        viewModel.UpdateFrom(row);
        var updates = Enumerable.Range(0, count)
            .Select(index => new RowTextAppended(row.RowId, delta, row.At.AddTicks(index))).ToArray();

        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var update in updates)
            viewModel.AppendText(update);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"4,096 paragraph appends: {allocated:N0} bytes allocated.");
        Assert.Equal(string.Concat(Enumerable.Repeat(delta, count)), viewModel.MarkdownText);
        Assert.True(allocated < 8_000_000, $"Append stream allocated {allocated:N0} bytes.");
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 64)]
    [InlineData(true, 64)]
    public void AppendText_UnbrokenTailAllocatesOnlyItsVisibleImmutableText(bool code, int coalescedDeltas)
    {
        const int count = 4096;
        var prefix = code ? "```text\n" : string.Empty;
        var delta = new string('x', 16 * coalescedDeltas);
        var row = Row(prefix, streaming: true);
        var viewModel = new AssistantMessageItemViewModel(row.RowId, row.Id);
        viewModel.UpdateFrom(row);
        var updates = Enumerable.Range(0, count / coalescedDeltas)
            .Select(index => new RowTextAppended(row.RowId, delta, row.At.AddTicks(index))).ToArray();

        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var update in updates)
            viewModel.AppendText(update);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"Unbroken {(code ? "code" : "prose")}, {updates.Length:N0} visible updates: {allocated:N0} bytes allocated.");
        var expected = prefix + new string('x', count * 16);
        Assert.Equal(expected, viewModel.StreamingTail);
        Assert.Equal(expected, viewModel.MarkdownText);
        Assert.Equal(code, viewModel.StreamingTailIsCode);
        Assert.True(allocated < (coalescedDeltas == 1 ? 300_000_000 : 6_000_000),
            $"Unbroken tail allocated {allocated:N0} bytes.");
        viewModel.UpdateFrom(row with
        {
            Parts = [new TextPart(StrongId.NewPartId(), expected)],
            IsStreaming = false,
        });
        Assert.Equal(expected, viewModel.FinalMarkdown);
        Assert.Empty(viewModel.StreamingTail);
    }

    private static AssistantMessageRow Row(string text, bool streaming) =>
        new("a1", StrongId.NewMessageId(),
            [new TextPart(StrongId.NewPartId(), text)], streaming, DateTimeOffset.UtcNow);
}
