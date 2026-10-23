using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Browser;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Sessions;
using Alcoler.Kernel.Runtime;
using Alcoler.Git;
using Alcoler.Terminal;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Alcoler.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Assert = Xunit.Assert;

namespace Alcoler.Tests.Views;

public class DocumentWorkspaceViewTests
{
    [AvaloniaFact]
    public async Task CustomControl_PreservesInstanceAndEditsAcrossActivationAndCollapse()
    {
        using var harness = new Harness(shell: true);
        var editor = new TrackingTextBox { Text = "Original text", AcceptsReturn = true };
        var first = harness.Open("extension-editor", "Extension editor", editor);
        var second = harness.Open("extension-border", "Extension border", new Border { Background = Brushes.Blue });
        await RenderAsync(harness.Window);

        await ClickAsync(Tab(harness.View, first), localPoint: new Point(10, 14));
        await RenderAsync(harness.Window);
        Assert.Same(first, harness.Documents.ActiveDocument);
        Assert.Same(editor, Body(harness.View, first).Content);
        Assert.Same(editor, Assert.Single(harness.View.GetVisualDescendants().OfType<TrackingTextBox>()));
        Assert.True(editor.IsEffectivelyVisible);
        Assert.True(editor.Bounds.Width > 0 && editor.Bounds.Height > 0);
        await ClickAsync(editor);
        Press(harness.Window, PhysicalKey.A, RawInputModifiers.Control);
        harness.Window.KeyTextInput("Edited in the extension");
        await RenderAsync(harness.Window);
        Assert.Equal("Edited in the extension", editor.Text);

        await ClickAsync(Tab(harness.View, second), localPoint: new Point(10, 14));
        await RenderAsync(harness.Window);
        Assert.False(editor.IsEffectivelyVisible);
        Assert.Same(editor, Body(harness.View, first).Content);
        await ClickAsync(Tab(harness.View, first), localPoint: new Point(10, 14));
        await RenderAsync(harness.Window);
        var body = Body(harness.View, first);
        await ClickAsync(Named<Button>(harness.View, "CollapseDocumentsButton"));
        await RenderAsync(harness.Window);

        Assert.False(harness.Documents.IsVisible);
        Assert.False(harness.View.IsEffectivelyVisible);
        Assert.Same(first, harness.Documents.ActiveDocument);
        Assert.Equal(2, harness.Documents.Items.Count);
        Assert.Equal(0, editor.DisposeCount);
        Press(harness.Window, PhysicalKey.B, RawInputModifiers.Control | RawInputModifiers.Alt);
        await RenderAsync(harness.Window);

        Assert.True(editor.IsEffectivelyVisible);
        Assert.Same(body, Body(harness.View, first));
        Assert.Same(editor, Body(harness.View, first).Content);
        Assert.Equal("Edited in the extension", editor.Text);
        Assert.Equal(0, editor.DisposeCount);
        await ClickAsync(CloseButton(Tab(harness.View, first)));
        await RenderAsync(harness.Window);
        Assert.Equal(1, editor.DisposeCount);
        Assert.Same(second, harness.Documents.ActiveDocument);
    }

    [AvaloniaFact]
    public async Task StatusPlan_OpensReadOnlyMarkdownDocument()
    {
        using var harness = new Harness();
        var plan = new StatusPlan
        {
            RowId = "plan-row",
            Title = "Plan title",
            Markdown = "# Plan title\n\nUse `ConversationMarkdown`.",
        };

        harness.Workbench.OpenStatusPlan(plan);
        await RenderAsync(harness.Window);

        var document = Assert.IsType<DocumentViewModel>(harness.Documents.ActiveDocument);
        Assert.Equal("plan:plan-row", document.Id);
        Assert.Equal("plan", document.Kind);
        Assert.Equal("Plan title", document.Title);
        var planView = Assert.Single(harness.View.GetVisualDescendants().OfType<PlanDocumentView>());
        Assert.True(planView.IsEffectivelyVisible);
        var markdown = Assert.Single(planView.GetVisualDescendants().OfType<Alcoler.Controls.ConversationMarkdown>());
        Assert.Equal(plan.Markdown, markdown.Markdown);
        Assert.True(markdown.IsEffectivelyVisible);
        Assert.Empty(planView.GetVisualDescendants().OfType<FileDocumentView>());
        var footer = Assert.Single(planView.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "Read-only · Markdown");
        Assert.True(footer.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task LocalDataTemplate_RendersUnknownViewModelAndRetainsItsView()
    {
        using var harness = new Harness();
        var built = new List<TextBox>();
        harness.View.DataTemplates.Add(new FuncDataTemplate<ExtensionContent>((content, _) =>
        {
            var editor = new TextBox { Text = content!.Text, AcceptsReturn = true };
            built.Add(editor);
            return editor;
        }));
        var content = new ExtensionContent("Template extension");
        var extension = harness.Open("unknown-kind", "Extension", content);
        await RenderAsync(harness.Window);
        var editor = Assert.Single(built);
        Assert.True(editor.IsEffectivelyVisible);
        Assert.Same(content, editor.DataContext);
        Assert.Equal("Template extension", editor.Text);
        editor.Text = "Retained local editor state";
        var body = Body(harness.View, extension);
        var other = harness.Open("other", "Other", new Border());
        await RenderAsync(harness.Window);
        Assert.False(editor.IsEffectivelyVisible);

        harness.Documents.Activate(extension);
        await RenderAsync(harness.Window);
        await ClickAsync(Named<Button>(harness.View, "CollapseDocumentsButton"));
        await RenderAsync(harness.Window);
        Assert.False(harness.View.IsEffectivelyVisible);
        harness.Documents.Activate(other);
        await RenderAsync(harness.Window);
        harness.Documents.Activate(extension);
        await RenderAsync(harness.Window);

        Assert.Single(built);
        Assert.Same(body, Body(harness.View, extension));
        Assert.Same(editor, Assert.Single(body.GetVisualDescendants().OfType<TextBox>()));
        Assert.Equal("Retained local editor state", editor.Text);
        Assert.True(editor.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task HeaderAndTabs_Keep48PixelChromeShrinkFrom156To60AndOverflow()
    {
        using var harness = new Harness();
        harness.Open("first", "First", new Border());
        await RenderAsync(harness.Window);
        Assert.Equal(48, Named<Border>(harness.View, "DocumentHeader").Bounds.Height);
        Assert.Equal(48, Named<ItemsControl>(harness.View, "DocumentBodies").TranslatePoint(default, harness.View)!.Value.Y);
        Assert.Equal(156, Assert.Single(TabPanel(harness.View).Children).Bounds.Width);

        for (var index = 1; index < 8; index++)
            harness.Open($"tab-{index}", $"Tab {index}", new Border());
        await RenderAsync(harness.Window);
        var scroll = Named<ScrollViewer>(harness.View, "DocumentTabsScroll");
        var panel = TabPanel(harness.View);
        var expectedWidth = scroll.Bounds.Width / 8;
        Assert.InRange(expectedWidth, 60.01, 155.99);
        Assert.All(panel.Children, child => Assert.InRange(child.Bounds.Width, expectedWidth - 1, expectedWidth + 1));

        harness.Window.Width = 1100;
        await RenderAsync(harness.Window);
        expectedWidth = Math.Clamp(scroll.Bounds.Width / 8, 60, 156);
        Assert.All(panel.Children, child => Assert.InRange(child.Bounds.Width, expectedWidth - 1, expectedWidth + 1));
        for (var index = 8; index < 24; index++)
            harness.Open($"tab-{index}", $"Tab {index}", new Border());
        await RenderAsync(harness.Window);
        Assert.All(panel.Children, child => Assert.Equal(60, child.Bounds.Width));
        Assert.True(scroll.Extent.Width > scroll.Viewport.Width);
        Assert.True(panel.Bounds.Width > scroll.Bounds.Width);
        Assert.Equal(48, Named<Border>(harness.View, "DocumentHeader").Bounds.Height);

        harness.Documents.Activate(harness.Documents.VisibleItems[0]);
        await RenderAsync(harness.Window);
        Assert.Equal(0, scroll.Offset.X, 2);
        var last = harness.Documents.VisibleItems[^1];
        harness.Documents.Activate(last);
        await RenderAsync(harness.Window);
        Assert.True(scroll.Offset.X > 0);
        var lastOrigin = Tab(harness.View, last).TranslatePoint(default, scroll)!.Value;
        Assert.True(lastOrigin.X >= -1 && lastOrigin.X + Tab(harness.View, last).Bounds.Width <= scroll.Bounds.Width + 1);
    }

    [AvaloniaFact]
    public async Task PointerSwitchAndInactiveClose_DoNotActivateTheClosedDocument()
    {
        using var harness = new Harness();
        var first = harness.Open("first", "First", new TextBox());
        var second = harness.Open("second", "Second", new TextBox());
        var third = harness.Open("third", "Third", new TextBox());
        await RenderAsync(harness.Window);
        await ClickAsync(Tab(harness.View, first), localPoint: new Point(10, 14));
        await RenderAsync(harness.Window);
        Assert.Same(first, harness.Documents.ActiveDocument);
        Assert.Contains("active", Tab(harness.View, first).Classes);
        Assert.True(Body(harness.View, first).IsEffectivelyVisible);
        Assert.False(Body(harness.View, second).IsEffectivelyVisible);
        var activations = new List<DocumentViewModel?>();
        harness.Documents.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DocumentWorkspaceViewModel.ActiveDocument))
                activations.Add(harness.Documents.ActiveDocument);
        };

        await ClickAsync(CloseButton(Tab(harness.View, third)));
        await RenderAsync(harness.Window);
        Assert.Empty(activations);
        Assert.Same(first, harness.Documents.ActiveDocument);
        Assert.DoesNotContain(third, harness.Documents.Items);
        Assert.Equal(new[] { first, second }, harness.Documents.VisibleItems.ToArray());
        await ClickAsync(Tab(harness.View, second), localPoint: new Point(10, 14));
        await RenderAsync(harness.Window);
        Assert.Same(second, harness.Documents.ActiveDocument);
        Assert.True(Body(harness.View, second).IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task MiddleClick_ClosesInactiveTabWithoutChangingSelection()
    {
        using var harness = new Harness();
        var first = harness.Open("first", "First", new Border());
        var second = harness.Open("second", "Second", new Border());
        await RenderAsync(harness.Window);
        await ClickAsync(Tab(harness.View, first), MouseButton.Middle, new Point(10, 14));
        await RenderAsync(harness.Window);

        Assert.Same(second, harness.Documents.ActiveDocument);
        Assert.Same(second, Assert.Single(harness.Documents.VisibleItems));
        Assert.DoesNotContain(Tabs(harness.View), tab => ReferenceEquals(tab.DataContext, first));
        Assert.True(Body(harness.View, second).IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task TabKeyboard_ArrowsSelectAndWrapAndDeleteClosesFocusedTab()
    {
        using var harness = new Harness();
        var first = harness.Open("first", "First", new Border());
        var second = harness.Open("second", "Second", new Border());
        var third = harness.Open("third", "Third", new Border());
        await RenderAsync(harness.Window);
        await ClickAsync(Tab(harness.View, first), localPoint: new Point(10, 14));
        Assert.True(Tab(harness.View, first).IsFocused);
        Press(harness.Window, PhysicalKey.ArrowRight);
        await RenderAsync(harness.Window);
        Assert.Same(second, harness.Documents.ActiveDocument);
        Assert.True(Tab(harness.View, second).IsFocused);
        Assert.True(Body(harness.View, second).IsEffectivelyVisible);
        Press(harness.Window, PhysicalKey.ArrowLeft);
        await RenderAsync(harness.Window);
        Assert.Same(first, harness.Documents.ActiveDocument);
        Assert.True(Tab(harness.View, first).IsFocused);
        Press(harness.Window, PhysicalKey.ArrowLeft);
        await RenderAsync(harness.Window);
        Assert.Same(third, harness.Documents.ActiveDocument);
        Assert.True(Tab(harness.View, third).IsFocused);

        Press(harness.Window, PhysicalKey.Delete);
        await RenderAsync(harness.Window);
        Assert.DoesNotContain(third, harness.Documents.Items);
        Assert.Same(second, harness.Documents.ActiveDocument);
        Assert.Equal(new[] { first, second }, harness.Documents.VisibleItems.ToArray());
    }

    [AvaloniaFact]
    public async Task PointerDrag_ReordersTabsWithoutReplacingTheirBodies()
    {
        using var harness = new Harness();
        var first = harness.Open("first", "First", new TextBox { Text = "Retained" });
        var second = harness.Open("second", "Second", new Border());
        var third = harness.Open("third", "Third", new Border());
        harness.Documents.Activate(first);
        await RenderAsync(harness.Window);
        var body = Body(harness.View, first);
        var start = Tab(harness.View, first).TranslatePoint(new Point(10, 14), harness.Window)!.Value;
        var end = Tab(harness.View, third).TranslatePoint(new Point(10, 14), harness.Window)!.Value;
        harness.Window.MouseMove(start);
        harness.Window.MouseDown(start, MouseButton.Left);
        harness.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        harness.Window.MouseUp(end, MouseButton.Left);
        await RenderAsync(harness.Window);

        Assert.Equal(new[] { second, third, first }, harness.Documents.VisibleItems.ToArray());
        Assert.Equal(new[] { second, third, first }, Tabs(harness.View).Select(tab => tab.DataContext).Cast<DocumentViewModel>().ToArray());
        Assert.Same(first, harness.Documents.ActiveDocument);
        Assert.Same(body, Body(harness.View, first));
        Assert.Equal("Retained", Assert.IsType<TextBox>(body.Content).Text);
    }

    [AvaloniaFact]
    public async Task Overview_SearchesVisibleTitlesCaseInsensitivelyAndActivatesResult()
    {
        using var harness = new Harness();
        var alpha = harness.Open("alpha", "Alpha source", new Border());
        harness.Open("beta", "Beta source", new Border());
        var alphabet = harness.Open("alphabet", "Alphabet notes", new Border());
        harness.Open("hidden", "Alpha from another owner", new Border(), ownerId: "another-owner");
        harness.Documents.SetContext(harness.Root, harness.OwnerId);
        await RenderAsync(harness.Window);
        var overview = Named<Button>(harness.View, "DocumentOverviewButton");
        await ClickAsync(overview);
        await RenderAsync(harness.Window);
        var flyout = Assert.IsType<Flyout>(overview.Flyout);
        Assert.True(flyout.IsOpen);
        var popup = Assert.IsAssignableFrom<Control>(flyout.Content);
        var search = Named<TextBox>(popup, "DocumentSearch");
        var items = Named<ItemsControl>(popup, "DocumentOverviewItems");
        await ClickAsync(search);
        TopLevel.GetTopLevel(search)!.KeyTextInput("  aLPHa  ");
        await RenderAsync(TopLevel.GetTopLevel(search)!);

        Assert.Equal(new[] { alpha, alphabet }, items.ItemsSource!.Cast<DocumentViewModel>().ToArray());
        var result = Assert.Single(items.GetVisualDescendants().OfType<Button>(), button => ReferenceEquals(button.DataContext, alpha));
        await ClickAsync(result);
        await RenderAsync(harness.Window);
        Assert.Same(alpha, harness.Documents.ActiveDocument);
        Assert.True(Body(harness.View, alpha).IsEffectivelyVisible);
        Assert.False(flyout.IsOpen);
    }

    [AvaloniaFact]
    public async Task ContextMenu_CloseOthersAndCloseAllAffectOnlyTheVisibleScope()
    {
        using var harness = new Harness();
        var first = harness.Open("first", "First", new Border());
        var retained = harness.Open("retained", "Retained", new Border());
        harness.Open("third", "Third", new Border());
        var shared = harness.Documents.Open(new DocumentViewModel("shared", "extension", "Shared", "M0 0L1 1",
            new Border(), harness.Root));
        var hiddenOwner = harness.Open("hidden-owner", "Other owner", new Border(), ownerId: "another-owner");
        var otherRoot = Path.Combine(harness.Root, "another-workspace");
        var hiddenRoot = harness.Open("hidden-root", "Other workspace", new Border(), workspaceRoot: otherRoot);
        harness.Documents.SetContext(harness.Root, harness.OwnerId);
        harness.Documents.Activate(first);
        await RenderAsync(harness.Window);

        var menu = await OpenContextMenuAsync(harness, retained);
        Assert.Same(first, harness.Documents.ActiveDocument);
        await ClickAsync(MenuItem(menu, "Close others"));
        await RenderAsync(harness.Window);
        Assert.Same(retained, Assert.Single(harness.Documents.VisibleItems));
        Assert.Same(retained, harness.Documents.ActiveDocument);
        Assert.DoesNotContain(shared, harness.Documents.Items);
        Assert.Equal(3, harness.Documents.Items.Count);
        Assert.Contains(hiddenOwner, harness.Documents.Items);
        Assert.Contains(hiddenRoot, harness.Documents.Items);

        menu = await OpenContextMenuAsync(harness, retained);
        await ClickAsync(MenuItem(menu, "Close all"));
        await RenderAsync(harness.Window);
        Assert.Empty(harness.Documents.VisibleItems);
        Assert.Null(harness.Documents.ActiveDocument);
        Assert.False(harness.Documents.HasDocuments);
        Assert.False(harness.Documents.IsVisible);
        Assert.Equal(2, harness.Documents.Items.Count);
        harness.Documents.SetContext(harness.Root, "another-owner");
        Assert.Same(hiddenOwner, Assert.Single(harness.Documents.VisibleItems));
        harness.Documents.SetContext(otherRoot, harness.OwnerId);
        Assert.Same(hiddenRoot, Assert.Single(harness.Documents.VisibleItems));
    }

    [AvaloniaFact]
    public async Task ContextMenu_MoveCommandsReorderVisibleTabs()
    {
        using var harness = new Harness();
        var first = harness.Open("first", "First", new Border());
        var second = harness.Open("second", "Second", new Border());
        var third = harness.Open("third", "Third", new Border());
        await RenderAsync(harness.Window);
        var menu = await OpenContextMenuAsync(harness, third);
        await ClickAsync(MenuItem(menu, "Move left"));
        await RenderAsync(harness.Window);
        Assert.Equal(new[] { first, third, second }, harness.Documents.VisibleItems.ToArray());
        menu = await OpenContextMenuAsync(harness, third);
        await ClickAsync(MenuItem(menu, "Move right"));
        await RenderAsync(harness.Window);
        Assert.Equal(new[] { first, second, third }, harness.Documents.VisibleItems.ToArray());
        Assert.Same(third, harness.Documents.ActiveDocument);
    }

    [AvaloniaFact]
    public async Task EmptyShellToggle_ShowsEmptyStateWithoutOpeningBuiltInDocuments()
    {
        using var harness = new Harness(shell: true);
        await RenderAsync(harness.Window);
        Assert.Empty(harness.Documents.Items);
        Assert.False(harness.Documents.IsVisible);
        Press(harness.Window, PhysicalKey.B, RawInputModifiers.Control | RawInputModifiers.Alt);
        await RenderAsync(harness.Window);

        Assert.True(harness.Documents.IsVisible);
        Assert.True(harness.View.IsEffectivelyVisible);
        Assert.True(Named<StackPanel>(harness.View, "DocumentEmptyState").IsEffectivelyVisible);
        Assert.False(harness.Documents.HasDocuments);
        Assert.Null(harness.Documents.ActiveDocument);
        Assert.Empty(Tabs(harness.View));
        Assert.Empty(harness.View.GetVisualDescendants().OfType<GitPaneView>());
        Assert.Empty(harness.View.GetVisualDescendants().OfType<BrowserPaneView>());
        Assert.Empty(harness.View.GetVisualDescendants().OfType<TerminalView>());
        await ClickAsync(Named<Button>(harness.View, "CollapseDocumentsButton"));
        await RenderAsync(harness.Window);
        Press(harness.Window, PhysicalKey.B, RawInputModifiers.Control | RawInputModifiers.Alt);
        await RenderAsync(harness.Window);
        Assert.True(Named<StackPanel>(harness.View, "DocumentEmptyState").IsEffectivelyVisible);
        Assert.Empty(harness.Documents.Items);
    }

    [AvaloniaFact]
    public async Task ShellCtrlW_WithComposerFocusClosesOnlyTheActiveExpandedRightDocument()
    {
        using var harness = new Harness(shell: true);
        var first = harness.Open("first", "First", new TextBox { Text = "First document" });
        var second = harness.Open("second", "Second", new TextBox { Text = "Second document" });
        await RenderAsync(harness.Window);
        var composer = Assert.Single(harness.Window.GetVisualDescendants().OfType<ComposerView>());
        var input = Named<TextBox>(composer, "InputBox");
        input.Text = "Keep the unsent composer draft";
        await ClickAsync(input);
        Assert.True(input.IsFocused);
        Press(harness.Window, PhysicalKey.W, RawInputModifiers.Control);
        await RenderAsync(harness.Window);

        Assert.DoesNotContain(second, harness.Documents.Items);
        Assert.Same(first, Assert.Single(harness.Documents.VisibleItems));
        Assert.Same(first, harness.Documents.ActiveDocument);
        Assert.True(input.IsFocused);
        Assert.Equal("Keep the unsent composer draft", input.Text);
        await ClickAsync(Named<Button>(harness.View, "CollapseDocumentsButton"));
        await RenderAsync(harness.Window);
        await ClickAsync(input);
        Press(harness.Window, PhysicalKey.W, RawInputModifiers.Control);
        await RenderAsync(harness.Window);
        Assert.Same(first, Assert.Single(harness.Documents.Items));
        Assert.False(harness.Documents.IsVisible);
        Assert.True(input.IsFocused);

        Press(harness.Window, PhysicalKey.B, RawInputModifiers.Control | RawInputModifiers.Alt);
        await RenderAsync(harness.Window);
        Assert.True(harness.Documents.IsVisible);
        Assert.True(input.IsFocused);
        Press(harness.Window, PhysicalKey.W, RawInputModifiers.Control);
        await RenderAsync(harness.Window);
        Assert.Empty(harness.Documents.Items);
        Assert.False(harness.Documents.IsVisible);
        Assert.Equal("Keep the unsent composer draft", input.Text);
        Assert.True(input.IsFocused);
        Assert.True(harness.Window.IsVisible);
    }

    [AvaloniaTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task AgentBrowserStatus_OpensRenderedOwnerDocumentWithoutManualBrowserOpen(bool statusBeforeBind)
    {
        var directory = Directory.CreateTempSubdirectory("alcoler-document-browser-").FullName;
        try
        {
            await using var store = new SqliteSessionStore(Path.Combine(directory, "sessions.sqlite"));
            await store.InitializeAsync(CancellationToken.None);
            var artifacts = new FileArtifactStore(Path.Combine(directory, "artifacts"));
            await using var runtime = new AgentRuntime(new AgentRuntimeOptions
            {
                Store = store,
                Artifacts = artifacts,
                Providers = [],
                Tools = [],
                WorkspaceFactory = root => new WorkspaceToolEnvironment(root, artifacts),
            });
            var session = await runtime.CreateSessionAsync(new SessionOptions(directory, "Browser owner"), CancellationToken.None);
            var other = await runtime.CreateSessionAsync(new SessionOptions(directory, "Other owner"), CancellationToken.None);
            using var controller = new InertBrowserController();
            var presenters = new List<(string TabId, Border Presenter)>();
            var rootBrowser = new BrowserPaneViewModel(controller, AvaloniaUiScheduler.Instance, pagePresenterFactory: tabId =>
            {
                var presenter = new Border { Child = new TextBlock { Text = $"Live page {tabId}" } };
                presenters.Add((tabId, presenter));
                return presenter;
            });
            using var harness = new Harness(shell: true, workbench: new WorkbenchViewModel(runtime,
                AvaloniaUiScheduler.Instance, new WorkbenchConfig([]), browserPane: rootBrowser));
            controller.Publish(other.Id, BrowserStatus("other-tab"));
            if (statusBeforeBind)
                controller.Publish(session.Id, BrowserStatus("agent-tab"));
            await RenderAsync(harness.Window);
            Assert.Empty(harness.Documents.Items);
            Assert.Empty(presenters);

            await harness.Workbench.OpenSessionAsync(session.Id);
            await RenderAsync(harness.Window);
            if (!statusBeforeBind)
            {
                Assert.Empty(harness.Documents.Items);
                Assert.False(harness.Documents.IsVisible);
                controller.Publish(session.Id, BrowserStatus("agent-tab"));
                await RenderAsync(harness.Window);
            }

            var document = Assert.Single(harness.Documents.Items);
            Assert.Equal("browser", document.Id);
            Assert.Equal("browser", document.Kind);
            Assert.Equal(session.Id.Value, document.OwnerId);
            Assert.Equal(DocumentViewModel.NormalizeWorkspaceRoot(directory), document.WorkspaceRoot);
            Assert.Same(document, harness.Documents.ActiveDocument);
            Assert.Same(document, Assert.Single(harness.Documents.VisibleItems));
            Assert.True(harness.Documents.IsVisible);
            Assert.True(harness.View.IsEffectivelyVisible);
            var browser = Assert.IsType<BrowserPaneViewModel>(document.Content);
            Assert.NotSame(rootBrowser, browser);
            Assert.Equal(session.Id, browser.ActiveSessionId);
            Assert.Equal("agent-tab", browser.ActiveTabId);
            var pane = Assert.Single(Body(harness.View, document).GetVisualDescendants().OfType<BrowserPaneView>());
            Assert.Same(browser, pane.DataContext);
            Assert.True(pane.IsEffectivelyVisible);
            Assert.True(pane.Bounds.Width > 0 && pane.Bounds.Height > 0);
            var created = Assert.Single(presenters);
            Assert.Equal("agent-tab", created.TabId);
            Assert.Same(created.Presenter, browser.PagePresenter);
            Assert.Contains(created.Presenter, pane.GetVisualDescendants());
            Assert.True(created.Presenter.IsEffectivelyVisible);
            Assert.True(created.Presenter.Bounds.Width > 0 && created.Presenter.Bounds.Height > 0);
            Assert.Null(rootBrowser.PagePresenter);
            Assert.False(rootBrowser.HasPagePresenter);

            var notes = harness.Workbench.OpenDocument(new DocumentViewModel("notes", "extension", "User notes", "M0 0L1 1",
                new TextBox { Text = "Keep my document selected" }, directory, session.Id.Value));
            await RenderAsync(harness.Window);
            controller.Publish(session.Id, BrowserStatus("next-agent-tab"));
            await RenderAsync(harness.Window);
            Assert.Same(notes, harness.Documents.ActiveDocument);
            Assert.True(Body(harness.View, notes).IsEffectivelyVisible);
            Assert.False(pane.IsEffectivelyVisible);
            Assert.Equal("next-agent-tab", browser.ActiveTabId);
            Assert.Same(document, Assert.Single(harness.Documents.Items, item => item.Id == "browser"));

            await ClickAsync(Named<Button>(harness.View, "CollapseDocumentsButton"));
            await RenderAsync(harness.Window);
            controller.Publish(session.Id, BrowserStatus("collapsed-agent-tab"));
            await RenderAsync(harness.Window);
            Assert.False(harness.Documents.IsVisible);
            Assert.False(harness.View.IsEffectivelyVisible);
            Assert.Same(notes, harness.Documents.ActiveDocument);
            harness.Documents.IsVisible = true;
            await RenderAsync(harness.Window);
            await ClickAsync(CloseButton(Tab(harness.View, document)));
            await RenderAsync(harness.Window);
            var presenterCount = presenters.Count;
            controller.Publish(session.Id, BrowserStatus("closed-agent-tab"));
            await RenderAsync(harness.Window);
            Assert.Same(notes, Assert.Single(harness.Documents.Items));
            Assert.Same(notes, harness.Documents.ActiveDocument);
            Assert.Equal(presenterCount, presenters.Count);
            Assert.Null(browser.PagePresenter);
            Assert.Null(rootBrowser.PagePresenter);
            Assert.Equal(0, controller.ActionCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static BrowserSessionStatus BrowserStatus(string tabId) => new(BrowserSessionState.Launched,
        UpdatedAt: DateTimeOffset.UtcNow,
        Tabs: [new BrowserTabStatus(tabId, $"https://fixture.test/{tabId}", $"Agent page {tabId}", IsActive: true)],
        ActiveTabId: tabId);

    [AvaloniaFact]
    public async Task NativeTemplates_RenderGitBrowserTextAndFakeTerminalWithoutStartingResources()
    {
        using var harness = new Harness();
        using var gitService = new InertGitService();
        using var browserController = new InertBrowserController();
        var terminalService = new InertTerminalService();
        var git = new GitPaneViewModel(harness.Root, gitService, InlineUiScheduler.Instance);
        var browser = new BrowserPaneViewModel(browserController, InlineUiScheduler.Instance);
        var text = new TextDocumentViewModel("Readme.txt", "Native text preview", "txt");
        var terminal = new TerminalPaneViewModel(harness.Root, terminalService, InlineUiScheduler.Instance);
        var gitDocument = harness.Open("git", "Changes", git);
        var browserDocument = harness.Open("browser", "Browser", browser);
        var textDocument = harness.Open("text", "Readme.txt", text);
        var terminalDocument = harness.Open("terminal", "Terminal", terminal);
        var views = new Dictionary<DocumentViewModel, Control>();
        foreach (var document in new[] { gitDocument, browserDocument, textDocument, terminalDocument })
        {
            harness.Documents.Activate(document);
            await RenderAsync(harness.Window);
            var body = Body(harness.View, document);
            Control view = document.Content switch
            {
                GitPaneViewModel => Assert.Single(body.GetVisualDescendants().OfType<GitPaneView>()),
                BrowserPaneViewModel => Assert.Single(body.GetVisualDescendants().OfType<BrowserPaneView>()),
                TextDocumentViewModel => Assert.Single(body.GetVisualDescendants().OfType<FileDocumentView>()),
                TerminalPaneViewModel => Assert.Single(body.GetVisualDescendants().OfType<TerminalView>()),
                _ => throw new InvalidOperationException("Unexpected native fixture."),
            };
            Assert.Same(document.Content, view.DataContext);
            Assert.True(view.IsEffectivelyVisible);
            Assert.True(view.Bounds.Width > 0 && view.Bounds.Height > 0);
            views.Add(document, view);
        }
        Assert.Equal("Native text preview", Named<SelectableTextBlock>(views[textDocument], "PART_DocumentText").Text);
        Assert.False(Named<Button>(views[textDocument], "PART_DocumentReload").IsVisible);
        Assert.Equal(0, terminalService.StartCount);
        Assert.Equal(0, browserController.ActionCount);
        Assert.Equal(0, gitService.CallCount);

        await ClickAsync(Named<Button>(views[terminalDocument], "PART_TerminalStart"));
        await RenderAsync(harness.Window);
        Assert.Equal(1, terminalService.StartCount);
        Assert.True(terminal.IsConnected);
        terminalService.Emit("retained terminal output");
        foreach (var document in new[] { textDocument, gitDocument, browserDocument, terminalDocument })
        {
            harness.Documents.Activate(document);
            await RenderAsync(harness.Window);
            Assert.Contains(views[document], Body(harness.View, document).GetVisualDescendants());
            Assert.True(views[document].IsEffectivelyVisible);
        }
        await ClickAsync(Named<Button>(harness.View, "CollapseDocumentsButton"));
        await RenderAsync(harness.Window);
        harness.Documents.Activate(terminalDocument);
        await RenderAsync(harness.Window);
        Assert.Contains(views[terminalDocument], Body(harness.View, terminalDocument).GetVisualDescendants());
        Assert.True(terminal.IsConnected);
        Assert.Equal(1, terminalService.StartCount);
        Assert.Equal(0, terminalService.DisposeCount);
        Assert.Equal(0, browserController.ActionCount);
        Assert.Equal(0, gitService.CallCount);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => control.Name == name);

    private static Border[] Tabs(DocumentWorkspaceView view) =>
        Named<ItemsControl>(view, "DocumentTabs").GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("documentTab")).ToArray();

    private static Border Tab(DocumentWorkspaceView view, DocumentViewModel document) =>
        Assert.Single(Tabs(view), tab => ReferenceEquals(tab.DataContext, document));

    private static Button CloseButton(Border tab) =>
        Assert.Single(tab.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("documentClose"));

    private static DocumentTabPanel TabPanel(DocumentWorkspaceView view) =>
        Assert.Single(Named<ItemsControl>(view, "DocumentTabs").GetVisualDescendants().OfType<DocumentTabPanel>());

    private static ContentControl Body(DocumentWorkspaceView view, DocumentViewModel document) =>
        Assert.Single(Named<ItemsControl>(view, "DocumentBodies").GetVisualDescendants().OfType<ContentControl>(),
            body => ReferenceEquals(body.Content, document.Content));

    private static MenuItem MenuItem(ContextMenu menu, string header) =>
        Assert.Single(menu.GetVisualDescendants().OfType<MenuItem>(), item => Equals(item.Header, header));

    private static async Task<ContextMenu> OpenContextMenuAsync(Harness harness, DocumentViewModel document)
    {
        var tab = Tab(harness.View, document);
        await ClickAsync(tab, MouseButton.Right, new Point(10, 14));
        await RenderAsync(harness.Window);
        var menu = Assert.IsType<ContextMenu>(tab.ContextMenu);
        Assert.True(menu.IsOpen);
        await RenderAsync(TopLevel.GetTopLevel(menu)!);
        return menu;
    }

    private static void Press(TopLevel root, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        root.KeyPressQwerty(key, modifiers);
        root.KeyReleaseQwerty(key, modifiers);
    }

    private static async Task ClickAsync(Control control, MouseButton button = MouseButton.Left, Point? localPoint = null)
    {
        var root = TopLevel.GetTopLevel(control)!;
        Assert.NotNull(root);
        await RenderAsync(root);
        Assert.True(control.IsEffectivelyVisible);
        Assert.True(control.IsEffectivelyEnabled);
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0);
        var point = control.TranslatePoint(localPoint ?? new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        Assert.True(new Rect(root.ClientSize).Contains(point), $"Expected {control.GetType().Name} at {point} inside {root.ClientSize}.");
        root.MouseMove(point);
        await RenderAsync(root);
        var hit = Assert.IsAssignableFrom<Visual>(root.InputHitTest(point));
        Assert.True(ReferenceEquals(hit, control) || hit.GetVisualAncestors().Contains(control),
            $"Expected {control.GetType().Name} at {point}, hit {hit.GetType().Name}.");
        root.MouseDown(point, button);
        root.MouseUp(point, button);
    }

    private static async Task RenderAsync(TopLevel root)
    {
        await SettlePaneAnimationsAsync(root);
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        var visual = ElementComposition.GetElementVisual(root);
        Assert.NotNull(visual);
        // Pointer hit testing requires a committed scene, not only completed layout.
        var rendered = visual.Compositor.RequestCompositionBatchCommitAsync().Rendered;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await rendered.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        await SettlePaneAnimationsAsync(root);
    }

    private static async Task SettlePaneAnimationsAsync(TopLevel root)
    {
        if (root is not MainWindow window)
        {
            return;
        }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (window.IsPaneAnimationActive)
        {
            Dispatcher.UIThread.RunJobs();
            root.UpdateLayout();
            Assert.True(DateTime.UtcNow < deadline, "Pane animation did not settle within 5 seconds.");
            await Task.Delay(10);
        }
    }

    private sealed class Harness : IDisposable
    {
        public Harness(bool shell = false, WorkbenchViewModel? workbench = null)
        {
            Workbench = workbench ?? ShellSmokeTests.CreateWorkbench();
            Root = Workbench.CurrentWorkspaceRoot;
            OwnerId = Workbench.CurrentDocumentOwnerId;
            if (shell)
            {
                Window = new MainWindow(Workbench);
                Window.Show();
                View = Assert.Single(Window.GetVisualDescendants().OfType<DocumentWorkspaceView>());
            }
            else
            {
                View = new DocumentWorkspaceView { DataContext = Workbench };
                View.Bind(Visual.IsVisibleProperty, new Binding("Documents.IsVisible"));
                Window = new Window { Width = 900, Height = 600, SizeToContent = SizeToContent.Manual, Content = View };
                Window.Show();
            }
        }

        public WorkbenchViewModel Workbench { get; }
        public DocumentWorkspaceViewModel Documents => Workbench.Documents;
        public Window Window { get; }
        public DocumentWorkspaceView View { get; }
        public string Root { get; }
        public string OwnerId { get; }

        public DocumentViewModel Open(string id, string title, object content, string? ownerId = null, string? workspaceRoot = null) =>
            Documents.Open(new DocumentViewModel(id, "extension", title, "M0 0L1 1", content,
                workspaceRoot ?? Root, ownerId ?? OwnerId));

        public void Dispose()
        {
            Window.Close();
            Workbench.Dispose();
        }
    }

    private sealed class TrackingTextBox : TextBox, IDisposable
    {
        protected override Type StyleKeyOverride => typeof(TextBox);
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed record ExtensionContent(string Text);

    private sealed class InertGitService : IGitService
    {
        public int CallCount { get; private set; }
        public string? FindRepositoryRoot(string workspaceRoot) { CallCount++; return null; }
        public string? GetCurrentBranchName(string repositoryRoot) { CallCount++; return null; }
        public GitRepositoryStatus GetStatus(string repositoryRoot) { CallCount++; return new([], [], []); }
        public IReadOnlyList<GitBranch> GetBranches(string repositoryRoot) { CallCount++; return []; }
        public IReadOnlyList<GitCommit> GetLog(string repositoryRoot, int limit) { CallCount++; return []; }
        public void Checkout(string repositoryRoot, string branchName) => CallCount++;
        public void Dispose() { }
    }

    private sealed class InertBrowserController : IBrowserController
    {
        private readonly Dictionary<SessionId, BrowserSessionStatus> _statuses = [];
        public int ActionCount { get; private set; }
        public event EventHandler<BrowserStatusChangedEventArgs>? StatusChanged;
        public void Publish(SessionId sessionId, BrowserSessionStatus status)
        {
            _statuses[sessionId] = status;
            StatusChanged?.Invoke(this, new BrowserStatusChangedEventArgs(sessionId, status));
        }
        public Task<BrowserActionResult> ExecuteAsync(SessionId sessionId, BrowserAction action, CancellationToken cancellationToken)
        {
            ActionCount++;
            return Task.FromResult(new BrowserActionResult(Ok: true));
        }
        public Task CancelAsync(SessionId sessionId, CancellationToken cancellationToken) { ActionCount++; return Task.CompletedTask; }
        public Task TakeoverAsync(SessionId sessionId, CancellationToken cancellationToken) { ActionCount++; return Task.CompletedTask; }
        public BrowserSessionStatus? GetStatus(SessionId sessionId) => _statuses.GetValueOrDefault(sessionId);
        public void Dispose() { }
    }

    private sealed class InertTerminalService : IPtyTerminalService
    {
        public int StartCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool IsRunning { get; private set; }
        public event EventHandler<string>? OutputReceived;
        public event EventHandler<int>? Exited { add { } remove { } }
        public void Emit(string output) => OutputReceived?.Invoke(this, output);
        public Task StartAsync(string workingDirectory, int cols, int rows, CancellationToken cancellationToken)
        {
            StartCount++;
            IsRunning = true;
            return Task.CompletedTask;
        }
        public Task WriteAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Resize(int cols, int rows) { }
        public Task StopAsync() { IsRunning = false; return Task.CompletedTask; }
        public void Dispose() { DisposeCount++; IsRunning = false; }
    }
}
