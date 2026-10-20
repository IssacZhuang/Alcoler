using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Alcoler.Workbench.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Headless;
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

    [AvaloniaFact]
    public void ModePicker_ClickSavesImmediately_AndNewWindowRestoresSelection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alcoler-mode-picker-{Guid.NewGuid():N}.json");
        try
        {
            var config = new WorkbenchConfig([]);
            config.Save(path);
            var runtime = new FakeAgentRuntime();
            using (var workbench = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, config,
                saveComposer: composer => (WorkbenchConfig.LoadOrDefault(path) with { Composer = composer }).Save(path)))
            {
                var window = new MainWindow(workbench);
                window.Show();
                try
                {
                    Dispatcher.UIThread.RunJobs();
                    var composer = Assert.Single(window.GetVisualDescendants().OfType<ComposerView>());
                    var picker = composer.FindControl<Button>("ModeSelector")!;
                    var pickerPoint = picker.TranslatePoint(new Point(picker.Bounds.Width / 2, picker.Bounds.Height / 2), window)!.Value;
                    window.MouseDown(pickerPoint, MouseButton.Left);
                    window.MouseUp(pickerPoint, MouseButton.Left);
                    Dispatcher.UIThread.RunJobs();
                    var flyout = Assert.IsType<Flyout>(picker.Flyout);
                    var panel = Assert.IsType<StackPanel>(flyout.Content);
                    var plan = Assert.Single(panel.Children.OfType<RadioButton>(), button => Equals(button.Content, "Plan"));
                    var topLevel = TopLevel.GetTopLevel(plan)!;
                    var point = plan.TranslatePoint(new Avalonia.Point(plan.Bounds.Width / 2, plan.Bounds.Height / 2), topLevel)!.Value;
                    topLevel.MouseDown(point, MouseButton.Left);
                    topLevel.MouseUp(point, MouseButton.Left);
                    Dispatcher.UIThread.RunJobs();

                    Assert.Equal(PermissionMode.Plan, workbench.Draft!.Composer.Mode);
                    Assert.Equal(PermissionMode.Plan, WorkbenchConfig.LoadOrDefault(path).Composer!.DefaultMode);
                    Assert.Empty(runtime.Submitted);
                }
                finally { window.Close(); }
            }
            using var restarted = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, WorkbenchConfig.LoadOrDefault(path));
            var reopened = new MainWindow(restarted);
            reopened.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var composer = Assert.Single(reopened.GetVisualDescendants().OfType<ComposerView>());
                Assert.Contains(composer.FindControl<Button>("ModeSelector")!.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == "Plan");
                Assert.Equal(PermissionMode.Plan, restarted.Draft!.Composer.Mode);
            }
            finally { reopened.Close(); }
        }
        finally { File.Delete(path); }
    }

    private static void PressNewTask(Window window) => window.RaiseEvent(new KeyEventArgs
    {
        RoutedEvent = InputElement.KeyDownEvent,
        Key = Key.N,
        KeyModifiers = KeyModifiers.Control,
    });
}
