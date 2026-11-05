using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

internal static class TaskText
{
    internal const string BashTaskType = "local_bash";
    internal const string SubagentTaskType = "local_agent";

    internal static string StatusText(BackgroundJobStatus status) => status switch
    {
        BackgroundJobStatus.Running => "running",
        BackgroundJobStatus.Completed => "completed",
        BackgroundJobStatus.Failed => "failed",
        BackgroundJobStatus.Stopped => "stopped",
        _ => status.ToString().ToLowerInvariant(),
    };

    internal static string SubagentStatusText(SubagentStatus status) => status switch
    {
        SubagentStatus.Running => "running",
        SubagentStatus.Completed => "completed",
        SubagentStatus.Failed => "failed",
        _ => "stopped",
    };

    internal static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}
