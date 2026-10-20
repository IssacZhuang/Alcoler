using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class RuntimeRemindersTests
{
    [Fact]
    public void FullReminder_StartsWithVerbatimGuardAndContainsAllPhases()
    {
        Assert.StartsWith(
            "Plan mode is active. The user indicated that they do not want you to execute yet -- you MUST NOT make any edits, run any non-readonly tools (including changing configs or making commits), or otherwise make any changes to the system. This supercedes any other instructions you have received.",
            RuntimeReminders.PlanModeFullReminder,
            StringComparison.Ordinal);
        Assert.Contains("## Plan Workflow", RuntimeReminders.PlanModeFullReminder);
        Assert.Contains("### Phase 1: Initial Understanding", RuntimeReminders.PlanModeFullReminder);
        Assert.Contains("### Phase 2: Design", RuntimeReminders.PlanModeFullReminder);
        Assert.Contains("### Phase 3: Review", RuntimeReminders.PlanModeFullReminder);
        Assert.Contains("### Phase 4: Call ExitPlanMode", RuntimeReminders.PlanModeFullReminder);
    }

    [Fact]
    public void FullReminder_UsesExploreAgentsAndResearchLimit()
    {
        Assert.Contains("you should only use the Explore subagent type", RuntimeReminders.PlanModeFullReminder);
        Assert.Contains("**Launch up to 3 Explore agents IN PARALLEL**", RuntimeReminders.PlanModeFullReminder);
        Assert.Contains("Quality over quantity - 3 agents maximum", RuntimeReminders.PlanModeFullReminder);
        Assert.Contains("3. Use AskUserQuestion to clarify any remaining questions with the user", RuntimeReminders.PlanModeFullReminder);
        Assert.Contains(
            "This is critical - your turn should only end with either using the AskUserQuestion tool OR calling ExitPlanMode. Do not stop unless it's for these 2 reasons",
            RuntimeReminders.PlanModeFullReminder);
    }

    [Fact]
    public void SparseReminder_IsVerbatim()
    {
        Assert.Equal(
            "Plan mode still active (see full instructions earlier in conversation). Read-only. Follow 4-phase workflow. End turns with AskUserQuestion (for clarifications) or ExitPlanMode (for plan approval). Never ask for plan approval via text or AskUserQuestion.",
            RuntimeReminders.PlanModeSparseReminder);
    }

    [Fact]
    public void ExitReminder_IsVerbatim()
    {
        Assert.Equal(
            "## Exited Plan Mode\n\nYou have exited plan mode. You can now make edits, run tools, and take actions.",
            RuntimeReminders.PlanModeExitReminder);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(11, true)]
    public void BuildModeReminder_FullOnFirstThenEveryFifth(int attachmentIndex, bool expectFull)
    {
        var reminder = RuntimeReminders.BuildModeReminder(attachmentIndex);
        Assert.Equal(expectFull, ReferenceEquals(reminder, RuntimeReminders.PlanModeFullReminder));
    }

    [Fact]
    public void IsPlanModeReminder_MatchesBothFormsOnly()
    {
        Assert.True(RuntimeReminders.IsPlanModeReminder(RuntimeReminders.PlanModeFullReminder));
        Assert.True(RuntimeReminders.IsPlanModeReminder(RuntimeReminders.PlanModeSparseReminder));
        Assert.False(RuntimeReminders.IsPlanModeReminder("## Exited Plan Mode"));
        Assert.False(RuntimeReminders.IsPlanModeReminder("plan mode is active"));
    }
}
