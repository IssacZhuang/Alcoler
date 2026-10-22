using Alcoler.Contracts.Identifiers;
using Alcoler.ViewModel;
using Xunit;

namespace Alcoler.Tests;

public sealed class SidebarSessionLimitTests
{
    [Fact]
    public void ShowMore_RevealsOnePageAtATime()
    {
        var sidebar = new SessionSidebarViewModel(() => Task.CompletedTask);
        sidebar.ApplySummaries(Sessions(12));

        var group = Assert.Single(sidebar.Groups);
        Assert.Equal(12, group.Sessions.Count);
        Assert.Equal(5, group.VisibleSessions.Count);
        Assert.True(group.HasSessionOverflow);
        Assert.True(group.CanShowMore);
        Assert.False(group.CanCollapse);

        group.ShowMore();

        Assert.Equal(10, group.VisibleSessions.Count);
        Assert.True(group.CanShowMore);
        Assert.True(group.CanCollapse);

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
    public void ApplySummaries_PreservesVisibleSessionCount()
    {
        var sidebar = new SessionSidebarViewModel(() => Task.CompletedTask);
        sidebar.ApplySummaries(Sessions(12));
        sidebar.Groups[0].ShowMore();

        sidebar.ApplySummaries(Sessions(12));

        var group = Assert.Single(sidebar.Groups);
        Assert.Equal(10, group.VisibleSessions.Count);
        Assert.True(group.CanShowMore);
        Assert.True(group.CanCollapse);
    }

    [Fact]
    public void GroupsWithFiveOrFewerSessions_HideShowMore()
    {
        var sidebar = new SessionSidebarViewModel(() => Task.CompletedTask);
        sidebar.ApplySummaries(Sessions(5));

        var group = Assert.Single(sidebar.Groups);
        Assert.Equal(5, group.VisibleSessions.Count);
        Assert.False(group.HasSessionOverflow);
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
