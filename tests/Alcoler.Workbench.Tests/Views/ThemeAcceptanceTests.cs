using System.Text.Json;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Tools;
using Alcoler.Workbench.Controls;
using Alcoler.Workbench.Stores;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Alcoler.Workbench.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorTextBlock.Avalonia;
using Xunit;
using Assert = Xunit.Assert;

namespace Alcoler.Workbench.Tests.Views;

public sealed class ThemeAcceptanceTests
{
    private const string Markdown = "## Border-free acceptance\n\nReadable **assistant text**, `inline code`, and [a link](https://example.invalid).\n\n> A neutral quotation.\n\n| Surface | State |\n| --- | --- |\n| Panel | Ready |\n\n```csharp\nvar ready = true;\n```\n\n---";
    private static readonly DateTimeOffset At = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Palette_UsesAlcoSurfacesAndAccessibleForegrounds(bool light)
    {
        var theme = Variant(light);
        var window = new Window { RequestedThemeVariant = theme };
        window.Show();
        try
        {
            await RenderAsync(window);
            foreach (var (key, dark, pale) in new (string, string, string)[]
            {
                ("Background", "#191919", "#F3F6F4"), ("Sidebar", "#252525", "#E6ECE8"),
                ("Chrome", "#191919", "#F3F6F4"), ("Panel", "#252525", "#FFFFFF"),
                ("Card", "#252525", "#FFFFFF"), ("Popover", "#2D2D2D", "#FFFFFF"),
                ("Input", "#343434", "#E1E9E4"), ("InputFocused", "#3E6058", "#D5E6DE"),
                ("Surface", "#343434", "#DCE6E0"), ("SurfaceHigh", "#414141", "#CFDBD3"),
                ("Hover", "#3E635A", "#D3E7DD"), ("Selected", "#344541", "#C5E3D6"),
                ("TextPrimary", "#DEDEDE", "#24382E"), ("TextSecondary", "#ADB6B1", "#425A4D"),
                ("TextTertiary", "#A8B3AC", "#4C6054"), ("Accent", "#35C2A1", "#08785F"),
                ("Brand", "#35C2A1", "#08785F"), ("BrandText", "#191919", "#FFFFFF"),
                ("AccentText", "#191919", "#FFFFFF"),
            })
                Assert.Equal(Color.Parse(light ? pale : dark), Resource(window, "Alcoler" + key));

            foreach (var key in new[] { "AlcolerBorder", "AlcolerBorderHover" })
                Assert.Equal(0, Resource(window, key).A);
            foreach (var surface in new[] { "Background", "Sidebar", "Panel", "Card", "Popover", "Input", "InputFocused", "Surface", "SurfaceHigh", "Hover", "Selected" })
                AssertContrast(Resource(window, "AlcolerTextPrimary"), Resource(window, "Alcoler" + surface), surface);
            foreach (var foreground in new[] { "TextSecondary", "TextTertiary" })
            foreach (var surface in new[] { "Background", "Sidebar", "Panel", "Card", "Popover", "Input" })
                AssertContrast(Resource(window, "Alcoler" + foreground), Resource(window, "Alcoler" + surface), foreground + "/" + surface);
            foreach (var fill in new[] { "Brand", "Accent", "Error" })
                AssertContrast(Resource(window, "Alcoler" + fill + "Text"), Resource(window, "Alcoler" + fill), fill + " fill");
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainWindow_ConversationQuestionsPermissionsAndPopupsStayBorderFree(bool light)
    {
        var theme = Variant(light);
        var previousTheme = Application.Current!.RequestedThemeVariant;
        Application.Current.RequestedThemeVariant = theme;
        var runtime = new FakeAgentRuntime();
        var id = StrongId.NewSessionId();
        var state = TestSnapshots.State(contextUsageTokens: 60_000);
        var projection = runtime.RegisterSession(id, new ConversationSnapshot(0, 1, [], state));
        using var store = new ProjectionStore(() => projection, id, InlineUiScheduler.Instance, state);
        using var workbench = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, Config());
        workbench.Sidebar.ApplySummaries([new SessionSummaryViewModel(id, "Alco theme acceptance", @"C:\acceptance", At)]);
        var window = new MainWindow(workbench) { Width = 1650, Height = 1000 };
        window.Show();
        try
        {
            await CheckAsync(window, $"main-{ThemeName(light)}-draft");
            var brandIcon = Named<Image>(window, "PART_BrandIcon");
            Assert.NotNull(brandIcon.Source);
            Assert.True(brandIcon.IsEffectivelyVisible);
            Assert.Equal(new Size(64, 64), brandIcon.Source.Size);
            Assert.Equal(new Size(20, 20), brandIcon.Bounds.Size);
            workbench.Active = new ConversationViewModel(runtime, store);
            workbench.Sidebar.SetActive(id);
            workbench.Active.Composer.Draft = "A readable follow-up message";
            var rows = new ConversationRow[]
            {
                new UserMessageRow("user", "Check the alco palette in both themes.", At),
                new AssistantMessageRow("assistant", StrongId.NewMessageId(), [new TextPart(StrongId.NewPartId(), Markdown)], false, At),
                new ToolCallRow("todo", StrongId.NewToolCallId(), "TodoWrite",
                    JsonSerializer.SerializeToElement(new { todos = new[] { new { content = "Verify rendered surfaces", status = "in_progress" } } }),
                    ToolCallStatus.Succeeded, [], false, TimeSpan.Zero, At),
            };
            workbench.Active.ApplyUpdate(new ProjectionUpdate(2, state, [], true, rows));
            await CheckAsync(window, $"main-{ThemeName(light)}-conversation");
            Application.Current.RequestedThemeVariant = Variant(!light);
            await CheckAsync(window);
            Assert.Equal(Variant(!light), window.ActualThemeVariant);
            Assert.Equal(Resource(window, "AlcolerChrome"), Brush(window.Background));
            Application.Current.RequestedThemeVariant = theme;
            await CheckAsync(window);
            Assert.Contains(window.GetVisualDescendants().OfType<ConversationMarkdown>(), view => view.IsEffectivelyVisible);
            var status = window.FindControl<ConversationStatusView>("SessionStatus")!;
            Assert.True(status.IsEffectivelyVisible);
            var statusMode = Named<Button>(status, "DisplayModeButton");
            var statusMenu = Assert.IsType<MenuFlyout>(statusMode.Flyout);
            statusMenu.ShowAt(statusMode);
            await CheckPopupAsync(window, Assert.Single(statusMenu.Items.OfType<MenuItem>()));
            statusMenu.Hide();

            var composer = window.FindControl<ComposerView>("MainComposer")!;
            var context = Named<ContextUsageView>(composer, "ContextUsageHost");
            Named<Button>(composer, "ContextTrigger").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var popup = context.FindControl<Popup>("ContextPopup")!;
            Assert.True(popup.IsOpen);
            await CheckPopupAsync(window, Assert.IsAssignableFrom<Control>(popup.Child));
            popup.IsOpen = false;

            var question = state with
            {
                Phase = TurnPhase.AwaitingInteraction,
                PendingInteraction = new InteractionRequest(StrongId.NewInteractionId(), "Layout choice",
                    [new("Layout", "Which layout should we use?", [new("Split", "Two readable panes", "```text\n[left] [right]\n```"), new("Stack", "One column", "[top]\n[bottom]")])]),
            };
            workbench.Active.ApplyUpdate(new ProjectionUpdate(3, question, [new StatePatched(question)], false));
            await CheckAsync(window, $"main-{ThemeName(light)}-question");
            Assert.True(Named<Border>(window, "QuestionCard").IsEffectivelyVisible);
            var option = window.GetVisualDescendants().OfType<Button>().First(button => button.Tag is QuestionOptionViewModel);
            await HoverAsync(option);
            option.Focus(NavigationMethod.Tab);
            await CheckAsync(window);

            var permission = state with
            {
                Phase = TurnPhase.AwaitingPermission,
                PendingPermission = new PermissionRequest(StrongId.NewInteractionId(), id, "Write",
                    JsonSerializer.SerializeToElement(new { file_path = "theme.txt" }), RiskLevel.Medium, "Save the verified theme"),
            };
            workbench.Active.ApplyUpdate(new ProjectionUpdate(4, permission, [new StatePatched(permission)], false));
            await CheckAsync(window, $"main-{ThemeName(light)}-permission");
            Assert.True(Named<Border>(window, "PermissionCard").IsEffectivelyVisible);
            window.RequestedThemeVariant = Variant(!light);
            await CheckAsync(window);
            window.RequestedThemeVariant = theme;
            await CheckAsync(window);
            var allow = Named<Button>(window, "AllowButton");
            AssertContrast(Brush(allow.Foreground), Brush(allow.Background), "Allow permission");
            Assert.Empty(runtime.Submitted);
        }
        finally
        {
            window.Close();
            Application.Current.RequestedThemeVariant = previousTheme;
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsAndModelConfiguration_RenderBorderFreeAcrossSectionsAndFocus(bool light)
    {
        var theme = Variant(light);
        var settings = new SettingsDialog(Config()) { RequestedThemeVariant = theme };
        settings.Show();
        try
        {
            await CheckAsync(settings, $"settings-{ThemeName(light)}-providers");
            var input = settings.GetVisualDescendants().OfType<TextBox>().Single(box => box.Watermark == "https://api.example.com");
            await HoverAsync(input);
            input.Focus(NavigationMethod.Tab);
            await CheckAsync(settings);
            var toggle = settings.GetVisualDescendants().OfType<ToggleSwitch>().First(control => control.IsEffectivelyVisible);
            toggle.Focus(NavigationMethod.Tab);
            await CheckAsync(settings);
            var add = settings.FindControl<Button>("AddProviderButton")!;
            var flyout = Assert.IsType<Flyout>(add.Flyout);
            flyout.ShowAt(add);
            await CheckPopupAsync(settings, Assert.IsAssignableFrom<Control>(flyout.Content));
            flyout.Hide();
            foreach (var section in new[] { "McpTab", "RuntimeTab", "AppearanceTab" })
            {
                var tab = settings.FindControl<RadioButton>(section)!;
                if (!tab.IsVisible) continue;
                tab.IsChecked = true;
                tab.Focus(NavigationMethod.Tab);
                await CheckAsync(settings);
            }
            await CheckAsync(settings, $"settings-{ThemeName(light)}-appearance");
            var picker = settings.FindControl<ComboBox>("ThemePicker")!;
            picker.IsDropDownOpen = true;
            await RenderAsync(settings);
            await CheckPopupAsync(settings, Named<Popup>(picker, "PART_Popup").Child!);
            picker.IsDropDownOpen = false;
            var viewModel = Assert.IsType<SettingsViewModel>(settings.DataContext);
            viewModel.Error = "Example validation message";
            await CheckAsync(settings);

            var editor = viewModel.Providers[0].CreateModelEditor(viewModel.Providers[0].Models[0]);
            editor.IsAdvancedExpanded = true;
            var model = new ModelConfigurationDialog(editor) { RequestedThemeVariant = theme };
            model.Show();
            try
            {
                await CheckAsync(model, $"settings-{ThemeName(light)}-model");
                var image = model.FindControl<ToggleButton>("ImageToggle")!;
                image.BringIntoView();
                await HoverAsync(image);
                image.Focus(NavigationMethod.Tab);
                await CheckAsync(model);
                model.FindControl<ToggleSwitch>("SmartConfigurationToggle")!.Focus(NavigationMethod.Tab);
                await CheckAsync(model);
                editor.Error = "Example model validation message";
                await CheckAsync(model);
            }
            finally { model.Close(); }
        }
        finally { settings.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultControls_HoverKeyboardFocusSelectionAndDropdownHaveNoFluentBorders(bool light)
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        var input = new TextBox { Text = "Normal input" };
        var combo = new ComboBox { ItemsSource = new[] { "First", "Second" }, SelectedIndex = 0 };
        var list = new ListBox { ItemsSource = new[] { "Selected row", "Other row" }, SelectedIndex = 0, Height = 100 };
        Control[] controls = [new Button { Content = "Default button" }, input, combo,
            new ToggleButton { Content = "Toggle", IsChecked = true }, new CheckBox { Content = "Check", IsChecked = true },
            new RadioButton { Content = "Radio", IsChecked = true }, new ToggleSwitch { OnContent = "On", IsChecked = true },
            list, new Expander { Header = "Expanded", Content = new TextBlock { Text = "Panel content" }, IsExpanded = true }];
        foreach (var control in controls) panel.Children.Add(control);
        var primary = new Button { Content = new TextBlock { Text = "Primary action" } };
        primary.Classes.Add("primary");
        panel.Children.Add(primary);
        var danger = new Button { Content = "Danger action" };
        danger.Classes.Add("danger");
        panel.Children.Add(danger);
        var window = new Window { RequestedThemeVariant = Variant(light), Width = 620, Height = 1000, Content = panel };
        window.Show();
        try
        {
            await CheckAsync(window);
            foreach (var filled in new[] { primary, danger })
                AssertContrast(Brush(filled.Foreground), Brush(filled.Background), string.Join(' ', filled.Classes));
            foreach (var control in controls.Concat([primary, danger]))
            {
                await HoverAsync(control);
                Assert.True(control.IsPointerOver, control.GetType().Name);
                await CheckAsync(window);
                control.Focus(NavigationMethod.Tab);
                await CheckAsync(window);
            }
            var inputBorder = Named<Border>(input, "PART_BorderElement");
            window.MouseMove(new Point(1, 1));
            input.Focus(NavigationMethod.Tab);
            await CheckAsync(window);
            Assert.Equal(Resource(window, "AlcolerInputFocused"), Brush(inputBorder.Background));
            AssertContrast(Brush(input.Foreground), Brush(inputBorder.Background), "Focused input");
            combo.IsDropDownOpen = true;
            await RenderAsync(window);
            var popup = Named<Popup>(combo, "PART_Popup");
            var child = Assert.IsAssignableFrom<Control>(popup.Child);
            await CheckPopupAsync(window, child);
            var item = child.GetVisualDescendants().OfType<ComboBoxItem>().Last();
            await HoverAsync(item);
            item.Focus(NavigationMethod.Tab);
            await CheckPopupAsync(window, child);
            combo.IsDropDownOpen = false;
            var tooltip = new ToolTip { Content = "Readable tooltip" };
            ToolTip.SetTip(primary, tooltip);
            ToolTip.SetIsOpen(primary, true);
            await CheckPopupAsync(window, tooltip);
            ToolTip.SetIsOpen(primary, false);
        }
        finally { window.Close(); }
    }

    private static WorkbenchConfig Config() => new(
        [new WorkbenchProviderConfig("openai-compatible", "https://example.invalid/v1", "test-only",
            [new WorkbenchModelConfig("acceptance-model", 200_000, SupportsImage: true, ReasoningLevels: ["low", "high"])],
            Id: "acceptance", Name: "Acceptance provider")],
        McpServers: [new WorkbenchMcpServerConfig("Example server", "example", [])]);

    private static ThemeVariant Variant(bool light) => light ? ThemeVariant.Light : ThemeVariant.Dark;
    private static string ThemeName(bool light) => light ? "light" : "dark";
    private static T Named<T>(Control root, string name) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => control.Name == name);
    private static Color Resource(Control control, string key) => Brush(Assert.IsAssignableFrom<IBrush>(control.FindResource(control.ActualThemeVariant, key)));
    private static Color Brush(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static async Task CheckAsync(TopLevel root, string? screenshot = null)
    {
        await RenderAsync(root);
        if (screenshot is not null)
        {
            using var frame = root.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.True(frame.PixelSize.Width > 0 && frame.PixelSize.Height > 0);
            var directory = Environment.GetEnvironmentVariable("ALCOLER_THEME_SCREENSHOTS");
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                frame.Save(Path.Combine(directory, screenshot + ".png"));
            }
        }
        AssertBorderFree(root);
        AssertReadableText(root);
        foreach (var icon in root.GetVisualDescendants().OfType<Icon>()
                     .Where(icon => icon.Classes.Contains("icon") && icon.IsEffectivelyVisible && icon.Fill is null))
        {
            Assert.True(icon.StrokeThickness >= 1, "Icon geometry must keep its strokes.");
            Assert.True(Brush(icon.Stroke).A > 0, "Icon strokes must remain visible.");
        }
    }

    private static async Task CheckPopupAsync(TopLevel owner, Control content)
    {
        await RenderAsync(owner);
        var root = TopLevel.GetTopLevel(content);
        Assert.NotNull(root);
        Assert.True(content.IsEffectivelyVisible);
        Assert.True(content.Bounds.Width > 0 && content.Bounds.Height > 0);
        Assert.Equal(owner.ActualThemeVariant, content.ActualThemeVariant);
        await CheckAsync(root);
        var surfaces = root.GetSelfAndVisualDescendants().Select(visual => visual switch
        {
            Border border => border.Background,
            TemplatedControl control => control.Background,
            _ => null,
        }).OfType<ISolidColorBrush>();
        Assert.Contains(surfaces, brush => brush.Color == Resource(owner, "AlcolerPopover"));
        AssertBorderFree(content);
    }

    private static void AssertBorderFree(Control root)
    {
        var failures = root.GetSelfAndVisualDescendants().OfType<Control>()
            .Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0)
            .Select(control => (Control: control, Thickness: control switch
            {
                Border border => border.BorderThickness,
                TemplatedControl templated => templated.BorderThickness,
                ContentPresenter presenter => presenter.BorderThickness,
                _ => default,
            }))
            .Where(item => item.Thickness != default)
            .Select(item => $"{item.Control.GetType().Name}#{item.Control.Name} [{string.Join(' ', item.Control.Classes)}]: {item.Thickness}").ToArray();
        Assert.True(failures.Length == 0, "Decorative borders must have zero thickness:\n" + string.Join('\n', failures));
    }

    private static void AssertReadableText(Control root)
    {
        List<string> failures = [];
        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            var (text, foreground) = control switch
            {
                TextBlock block => (block.Text, block.Foreground),
                CTextBlock block => (block.Text, block.Foreground),
                TextPresenter presenter => (presenter.Text, presenter.Foreground),
                _ => (null, null),
            };
            if (string.IsNullOrWhiteSpace(text) || foreground is not ISolidColorBrush brush || !IsOpaqueEnabled(control)) continue;
            var background = Background(control);
            var color = Composite(brush.Color, background);
            var contrast = Contrast(color, background);
            if (contrast < 4.5)
                failures.Add($"{control.GetType().Name}#{control.Name} '{text[..Math.Min(text.Length, 60)]}': {color} on {background} = {contrast:F2}:1");
        }
        Assert.True(failures.Count == 0, "Normal text must reach 4.5:1 on its rendered surface:\n" + string.Join('\n', failures.Take(30)));
    }

    private static bool IsOpaqueEnabled(Control control) => control.IsEffectivelyVisible && control.IsEffectivelyEnabled
        && control.Bounds.Width > 0 && control.Bounds.Height > 0
        && control.GetSelfAndVisualAncestors().All(visual => visual.Opacity >= .999);

    private static Color Background(Control control)
    {
        var background = Colors.Transparent;
        foreach (var visual in control.GetSelfAndVisualAncestors().Reverse())
        {
            var brush = visual switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                TemplatedControl templated => templated.Background,
                ContentPresenter presenter => presenter.Background,
                TextBlock block => block.Background,
                CTextBlock block => block.Background,
                _ => null,
            };
            if (brush is ISolidColorBrush solid) background = Composite(solid.Color, background);
        }
        Assert.Equal(255, background.A);
        return background;
    }

    private static Color Composite(Color foreground, Color background)
    {
        var alpha = foreground.A / 255d;
        var backAlpha = background.A / 255d * (1 - alpha);
        var combined = alpha + backAlpha;
        if (combined == 0) return Colors.Transparent;
        byte Channel(byte front, byte back) => (byte)Math.Round((front * alpha + back * backAlpha) / combined);
        return Color.FromArgb((byte)Math.Round(combined * 255), Channel(foreground.R, background.R), Channel(foreground.G, background.G), Channel(foreground.B, background.B));
    }

    private static double Contrast(Color foreground, Color background)
    {
        static double Linear(byte channel) => channel / 255d <= .04045 ? channel / 255d / 12.92 : Math.Pow((channel / 255d + .055) / 1.055, 2.4);
        static double Luminance(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        var front = Luminance(foreground);
        var back = Luminance(background);
        return (Math.Max(front, back) + .05) / (Math.Min(front, back) + .05);
    }

    private static void AssertContrast(Color foreground, Color background, string description) =>
        Assert.True(Contrast(foreground, background) >= 4.5, $"{description}: {foreground} on {background} = {Contrast(foreground, background):F2}:1");

    private static async Task HoverAsync(Control control)
    {
        var root = TopLevel.GetTopLevel(control)!;
        control.BringIntoView();
        await RenderAsync(root);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseMove(point);
        await RenderAsync(root);
    }

    private static async Task RenderAsync(TopLevel root)
    {
        await SettlePaneAnimationsAsync(root);
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        var visual = ElementComposition.GetElementVisual(root)!;
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
}
