using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

public static class StreamingToolPolicy
{
    public static bool CanExecuteDuringStream(ITool tool) =>
        tool.Metadata.ReadOnly
        && tool.Metadata.ConcurrentSafe
        && !tool.Metadata.Destructive
        && !tool.Metadata.NeedsApproval
        && !tool.Metadata.RequiresUserInteraction
        && tool.Metadata.SideEffectScope == SideEffectScope.None;
}
