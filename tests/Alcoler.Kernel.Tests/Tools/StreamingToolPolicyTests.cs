using Xunit;
using Alcoler.Adapters.Tools;
using Alcoler.Kernel.Tools;

namespace Alcoler.Kernel.Tests.Tools;

public class StreamingToolPolicyTests
{
    [Fact]
    public void ReadOnlyConcurrentSafeScopeNone_CanExecuteDuringStream()
    {
        var readFile = new ReadTool();
        var glob = new GlobTool();
        var grep = new GrepTool();

        Assert.True(StreamingToolPolicy.CanExecuteDuringStream(readFile));
        Assert.True(StreamingToolPolicy.CanExecuteDuringStream(glob));
        Assert.True(StreamingToolPolicy.CanExecuteDuringStream(grep));
    }

    [Fact]
    public void SideEffectingTools_CannotExecuteDuringStream()
    {
        Assert.False(StreamingToolPolicy.CanExecuteDuringStream(new WriteTool()));
        Assert.False(StreamingToolPolicy.CanExecuteDuringStream(new EditTool()));
        Assert.False(StreamingToolPolicy.CanExecuteDuringStream(new BashTool()));
    }

    [Fact]
    public void NeedsApproval_OrUserInteraction_BlocksDuringStream()
    {
        var needsApproval = new FakeTool("Ask", FakeToolMetadata.ParallelSafe() with { NeedsApproval = true });
        var interactive = new FakeTool("Chat", FakeToolMetadata.ParallelSafe() with { RequiresUserInteraction = true });
        var destructive = new FakeTool("Boom", FakeToolMetadata.ParallelSafe() with { Destructive = true });
        var notConcurrent = new FakeTool("Solo", FakeToolMetadata.ParallelSafe() with { ConcurrentSafe = false });

        Assert.False(StreamingToolPolicy.CanExecuteDuringStream(needsApproval));
        Assert.False(StreamingToolPolicy.CanExecuteDuringStream(interactive));
        Assert.False(StreamingToolPolicy.CanExecuteDuringStream(destructive));
        Assert.False(StreamingToolPolicy.CanExecuteDuringStream(notConcurrent));
    }
}
