using Alcoler.Contracts.Identifiers;
using Alcoler.Workbench.ViewModel;
using Xunit;

namespace Alcoler.Workbench.Tests;

public sealed class SidebarSessionLimitTests
{
    [Fact]
    public void ApplySummaries_LimitsVisibleSessionsUntilShowMore()
    {
        var sidebar = new SessionSidebarViewModel(() => Task.CompletedTask);
        sidebar.ApplySummaries(Sessions(8));

        var group = Assert.Single(sidebar.Groups);
        Assert.Equal(8, group.Sessions.Count);
        Assert.Equal(5, group.VisibleSessions.Count);
        Assert.True(group.HasHiddenSessions);
        Assert.True(group.CanShowMore);
        Assert.False(group.CanCollapse);

        group.ShowMore();

        Assert.Equal(group.Sessions.ToArray(), group.VisibleSessions.ToArray());
        Assert.False(group.CanShowMore);
        Assert.True(group.CanCollapse);

        group.Collapse();

        Assert.Equal(group.Sessions.Take(5).ToArray(), group.VisibleSessions.ToArray());
        Assert.True(group.CanShowMore);
        Assert.False(group.CanCollapse);
    }

    [Fact]
    public void ApplySummaries_PreservesShowingAllState()
    {
        var sidebar = new SessionSidebarViewModel(() => Task.CompletedTask);
        sidebar.ApplySummaries(Sessions(7));
        sidebar.Groups[0].ShowMore();

        sidebar.ApplySummaries(Sessions(7));

        var group = Assert.Single(sidebar.Groups);
        Assert.True(group.IsShowingAll);
        Assert.Equal(7, group.VisibleSessions.Count);
    }

    [Fact]
    public void GroupsWithFiveOrFewerSessions_HideShowMore()
    {
        var sidebar = new SessionSidebarViewModel(() => Task.CompletedTask);
        sidebar.ApplySummaries(Sessions(5));

        var group = Assert.Single(sidebar.Groups);
        Assert.Equal(5, group.VisibleSessions.Count);
        Assert.False(group.HasHiddenSessions);
        Assert.False(group.CanShowMore);
        Assert.False(group.CanCollapse);
    }

    private static List<SessionSummaryViewModel> Sessions(int count) =>
        Enumerable.Range(0, count)
            .Select(index => new SessionSummaryViewModel(
                StrongId.NewSessionId(),
                $"Session {index}",
                @"C:\ws\alpha",
                DateTimeOffset.UtcNow.AddMinutes(-index)))
            .ToList();
}
