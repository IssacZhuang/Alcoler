using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.ViewModel;
using Xunit;

namespace Alcoler.Tests.ViewModel;

public class AssistantStreamingTests
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

    private static AssistantMessageRow Row(string text, bool streaming) =>
        new("a1", StrongId.NewMessageId(),
            [new TextPart(StrongId.NewPartId(), text)], streaming, DateTimeOffset.UtcNow);
}
