using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Workbench.Stores;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Assert = Xunit.Assert;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(Alcoler.Workbench.Tests.Views.ShellTestApp))]

namespace Alcoler.Workbench.Tests.Views;

/// <summary>
/// Headless app for the assembly: without it the framework boots an empty Application
/// with no Fluent theme and no Alcoler tokens, which the shell views depend on.
/// </summary>
public sealed class ShellTestApp : Avalonia.Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        var theme = (ResourceDictionary)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
            new System.Uri("avares://Alcoler.Workbench/Themes/AlcolerTheme.axaml"));
        Resources.MergedDictionaries.Add(theme);
    }

    public static Avalonia.AppBuilder BuildAvaloniaApp() =>
        Avalonia.AppBuilder.Configure<ShellTestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class ShellSmokeTests
{
    private static readonly string[] TokenKeys =
    [
        "AlcolerBackground",
        "AlcolerSurface",
        "AlcolerSurfaceHigh",
        "AlcolerBorder",
        "AlcolerTextPrimary",
        "AlcolerTextSecondary",
        "AlcolerAccent",
        "AlcolerAccentText",
        "AlcolerSuccess",
        "AlcolerWarning",
        "AlcolerError",
        "AlcolerInfo",
    ];

    [AvaloniaFact]
    public void MainWindow_Constructs_And_HostsAllRegions()
    {
        var viewModel = CreateWorkbench();
        var window = new MainWindow(viewModel);
        window.Show();

        Assert.IsType<Workbench.Views.SidebarView>(Assert.Single(window.GetVisualDescendants().OfType<Workbench.Views.SidebarView>()));
        Assert.IsType<Workbench.Views.TimelineView>(Assert.Single(window.GetVisualDescendants().OfType<Workbench.Views.TimelineView>()));
        Assert.IsType<Workbench.Views.InteractionAreaView>(Assert.Single(window.GetVisualDescendants().OfType<Workbench.Views.InteractionAreaView>()));
        Assert.IsType<Workbench.Views.ComposerView>(Assert.Single(window.GetVisualDescendants().OfType<Workbench.Views.ComposerView>()));
        window.Close();
    }

    [AvaloniaFact]
    public void Theme_TokensResolveInDarkAndLight()
    {
        var window = new Window();
        window.Show();

        foreach (var key in TokenKeys)
        {
            Assert.True(window.TryFindResource(key, ThemeVariant.Dark, out var dark), key);
            var darkBrush = Assert.IsType<SolidColorBrush>(dark);
            Assert.NotEqual(default, darkBrush.Color);

            Assert.True(window.TryFindResource(key, ThemeVariant.Light, out var light), key);
            var lightBrush = Assert.IsType<SolidColorBrush>(light);
            Assert.NotEqual(darkBrush.Color, lightBrush.Color);
        }

        window.Close();
    }

    [AvaloniaFact]
    public void Sidebar_RendersGroupsSessionsAndActiveState()
    {
        var viewModel = CreateWorkbench();
        var alphaA = new SessionSummaryViewModel(NewId(), "Session A", @"C:\ws\alpha", DateTimeOffset.UtcNow.AddMinutes(-5));
        var alphaB = new SessionSummaryViewModel(NewId(), "Session B", @"C:\ws\alpha", DateTimeOffset.UtcNow.AddMinutes(-1));
        var betaC = new SessionSummaryViewModel(NewId(), "Session C", @"C:\ws\beta", DateTimeOffset.UtcNow.AddHours(-2));
        viewModel.Sidebar.ApplySummaries([alphaA, alphaB, betaC]);
        viewModel.Sidebar.SetActive(alphaB.Id);

        var window = new MainWindow(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var sidebar = Assert.IsType<Workbench.Views.SidebarView>(
            Assert.Single(window.GetVisualDescendants().OfType<Workbench.Views.SidebarView>()));
        var buttons = sidebar.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.Classes.Contains("sessionItem"))
            .ToList();
        Assert.Equal(3, buttons.Count);

        var titles = buttons
            .Select(b => b.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Classes.Contains("sessionTitle")).Text)
            .ToList();
        Assert.Equal(new[] { "Session B", "Session A", "Session C" }, titles);

        var indicators = sidebar.GetVisualDescendants()
            .OfType<Border>()
            .Where(b => b.Name == "PART_ActiveIndicator")
            .ToList();
        Assert.Equal(3, indicators.Count);
        Assert.Single(indicators, i => i.IsVisible);

        var empty = sidebar.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(t => t.Name == "PART_SidebarEmpty");
        Assert.False(empty.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void Sidebar_ShowsEmptyState_WhenNoSessions()
    {
        var viewModel = CreateWorkbench();
        var window = new MainWindow(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var sidebar = Assert.IsType<Workbench.Views.SidebarView>(
            Assert.Single(window.GetVisualDescendants().OfType<Workbench.Views.SidebarView>()));
        Assert.DoesNotContain(sidebar.GetVisualDescendants().OfType<Button>(),
            b => b.Classes.Contains("sessionItem"));
        var empty = sidebar.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(t => t.Name == "PART_SidebarEmpty");
        Assert.True(empty.IsVisible);
        Assert.Equal("No sessions yet", empty.Text);

        window.Close();
    }

    [AvaloniaFact]
    public void Header_MeterAndPhase_TrackStatePatch()
    {
        var (viewModel, store) = CreateWorkbenchWithoutPump();
        var window = new MainWindow(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var fill = window.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Name == "PART_ContextMeterFill");
        var phase = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "PART_PhaseText");

        Assert.Equal(0d, fill.Width);

        var low = TestSnapshots.State(TurnPhase.Streaming, contextUsageTokens: 60_000);
        viewModel.Active!.ApplyUpdate(new ProjectionUpdate(2, low, [new StatePatched(low)], Reset: false));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Streaming", phase.Text);
        Assert.Equal(25.2d, fill.Width, 3);
        var lowBrush = Assert.IsType<SolidColorBrush>(fill.Background);
        Assert.Equal(Color.Parse("#FF3FB950"), lowBrush.Color);

        var mid = TestSnapshots.State(TurnPhase.ExecutingTools, contextUsageTokens: 120_000);
        viewModel.Active!.ApplyUpdate(new ProjectionUpdate(3, mid, [new StatePatched(mid)], Reset: false));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("ExecutingTools", phase.Text);
        Assert.Equal(50.4d, fill.Width, 3);
        var midBrush = Assert.IsType<SolidColorBrush>(fill.Background);
        Assert.Equal(Color.Parse("#FFD29922"), midBrush.Color);

        var hot = TestSnapshots.State(TurnPhase.Error, contextUsageTokens: 180_000);
        viewModel.Active!.ApplyUpdate(new ProjectionUpdate(4, hot, [new StatePatched(hot)], Reset: false));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Error", phase.Text);
        Assert.Equal(75.6d, fill.Width, 3);
        var hotBrush = Assert.IsType<SolidColorBrush>(fill.Background);
        Assert.Equal(Color.Parse("#FFF85149"), hotBrush.Color);

        window.Close();
        viewModel.Dispose();
        store.Dispose();
    }

    internal static WorkbenchViewModel CreateWorkbench()
    {
        var runtime = new FakeAgentRuntime();
        var sessionId = new SessionId(Guid.NewGuid().ToString("N"));
        runtime.RegisterSession(sessionId, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        var viewModel = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, new WorkbenchConfig([]));
        viewModel.OpenSessionAsync(sessionId).GetAwaiter().GetResult();
        return viewModel;
    }

    /// <summary>Workbench whose projection store is never started, so state changes are driven only by direct patches.</summary>
    private static (WorkbenchViewModel ViewModel, ProjectionStore Store) CreateWorkbenchWithoutPump()
    {
        var runtime = new FakeAgentRuntime();
        var sessionId = new SessionId(Guid.NewGuid().ToString("N"));
        var projection = runtime.RegisterSession(sessionId, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        var store = new ProjectionStore(() => projection, sessionId, InlineUiScheduler.Instance, TestSnapshots.State());
        var viewModel = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, new WorkbenchConfig([]));
        viewModel.Active = new ConversationViewModel(runtime, store);
        return (viewModel, store);
    }

    private static SessionId NewId() => new(Guid.NewGuid().ToString("N"));
}
