using Alcoler.Contracts.Identifiers;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Alcoler.Workbench.Views;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Alcoler.Workbench.Tests.Views;

public sealed class DraftConversationSmokeTests
{
    [AvaloniaFact]
    public void NewTask_ImmediatelyShowsCenteredRealComposerWithoutDialog()
    {
        using var workbench = new WorkbenchViewModel(new FakeAgentRuntime(), InlineUiScheduler.Instance, new WorkbenchConfig([]));
        var window = new MainWindow(workbench);
        window.Show();
        try
        {
            window.FindControl<SidebarView>("Sidebar")!.FindControl<Button>("PART_NewSession")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(window.OwnedWindows);
            Assert.Null(workbench.Active);
            var draft = Assert.IsType<DraftConversationViewModel>(workbench.Draft);
            var composer = Assert.Single(window.GetVisualDescendants().OfType<ComposerView>());
            Assert.Same(draft, composer.DataContext);
            Assert.True(window.FindControl<StackPanel>("DraftHeader")!.IsVisible);
            Assert.Equal(Avalonia.Layout.VerticalAlignment.Bottom,
                window.FindControl<StackPanel>("DraftHeader")!.VerticalAlignment);
            Assert.True(window.FindControl<Grid>("ConversationArea")!.RowDefinitions[2].Height.IsStar);
            composer.FindControl<TextBox>("InputBox")!.Text = "Real bound draft input";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Real bound draft input", draft.Composer.Draft);
            Assert.True(composer.FindControl<Button>("SendButton")!.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CtrlN_UsesDraftInsteadOfModal_AndPreservesText()
    {
        using var workbench = ShellSmokeTests.CreateWorkbench();
        var window = new MainWindow(workbench);
        window.Show();
        try
        {
            PressNewTask(window);
            var draft = Assert.IsType<DraftConversationViewModel>(workbench.Draft);
            draft.Composer.Draft = "Preserve this draft";
            PressNewTask(window);
            Dispatcher.UIThread.RunJobs();

            Assert.Same(draft, workbench.Draft);
            Assert.Equal("Preserve this draft", workbench.Draft!.Composer.Draft);
            Assert.Empty(window.OwnedWindows);
            Assert.Null(workbench.Sidebar.ActiveSessionId);
            Assert.True(window.FindControl<StackPanel>("DraftHeader")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ProjectNewTask_ChoosesItsWorkspace_AndHeaderOffersNativeFolderAndOutsideProject()
    {
        using var workbench = new WorkbenchViewModel(new FakeAgentRuntime(), InlineUiScheduler.Instance, new WorkbenchConfig([]));
        var root = Path.Combine(Path.GetTempPath(), "alcoler-headless-project");
        workbench.Sidebar.ApplySummaries([
            new SessionSummaryViewModel(StrongId.NewSessionId(), "Existing task", root, DateTimeOffset.UtcNow),
        ]);
        var window = new MainWindow(workbench);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                control => control.Classes.Contains("projectNewTask"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(root, workbench.Draft!.WorkspaceRoot);
            Assert.Equal("alcoler-headless-project", workbench.Draft.WorkspaceName);
            Assert.NotNull(window.FindControl<Button>("WorkspaceChooser")!.Flyout);
            Assert.Equal("Open Folder...", window.FindControl<Button>("OpenFolderButton")!.Content);
            Assert.Equal("Work outside project", window.FindControl<Button>("OutsideProjectButton")!.Content);
            Assert.Empty(window.OwnedWindows);
        }
        finally
        {
            window.Close();
        }
    }

    private static void PressNewTask(Window window) => window.RaiseEvent(new KeyEventArgs
    {
        RoutedEvent = InputElement.KeyDownEvent,
        Key = Key.N,
        KeyModifiers = KeyModifiers.Control,
    });
}
