using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Controls;
using Alcoler.Stores;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Alcoler.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Rendering.Composition;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Alcoler.Tests;

public sealed class ToolImageTests
{
    [Theory]
    [InlineData("Read")]
    [InlineData("Browser")]
    public void Create_FiltersImageMediaAndPreservesMetadataAndText(string toolName)
    {
        var first = new ToolResultMedia("alcoler-artifact://test/first.png", "image/png", "first.png");
        var second = new ToolResultMedia("alcoler-artifact://test/second.webp", "IMAGE/WEBP");
        var row = ToolRow() with
        {
            ToolName = toolName,
            Status = ToolCallStatus.Succeeded,
            Output =
            [
                new ToolResultText("before\nimage"),
                new ToolResultMedia("alcoler-artifact://test/document.pdf", "application/pdf", "document.pdf"),
                first,
                new ToolResultMedia("alcoler-artifact://test/movie.mp4", "video/mp4", "movie.mp4"),
                new ToolResultText("after"),
                second,
                new ToolResultMedia("alcoler-artifact://test/notes.txt", "text/plain", "notes.txt"),
                new ToolResultMedia("alcoler-artifact://test/not-image", "image-like/type"),
            ],
        };

        var item = Assert.IsType<ToolCallItemViewModel>(TimelineItemFactory.Create(row));

        Assert.True(item.HasImages);
        Assert.Equal(2, item.ImageMedia.Count);
        Assert.Same(first, item.ImageMedia[0]);
        Assert.Same(second, item.ImageMedia[1]);
        Assert.Equal("before\nimage\nafter", item.OutputText);
        Assert.False(item.IsExpanded);
    }

    [Fact]
    public void UpdateFrom_PopulatesReplacesAndClearsObservableImages()
    {
        var pending = ToolRow();
        var item = Assert.IsType<ToolCallItemViewModel>(TimelineItemFactory.Create(pending));
        List<string?> changes = [];
        item.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        Assert.Empty(item.ImageMedia);
        Assert.False(item.HasImages);

        var first = new ToolResultMedia("alcoler-artifact://test/first.png", "image/png", "first.png");
        var complete = pending with
        {
            Status = ToolCallStatus.Succeeded,
            Output = [new ToolResultText("image read"), first],
        };
        Assert.True(TimelineItemFactory.Update(item, complete));
        Assert.Same(first, Assert.Single(item.ImageMedia));
        Assert.True(item.HasImages);
        Assert.Equal("image read", item.OutputText);
        Assert.Equal(ToolCallStatus.Succeeded, item.Status);
        AssertImageNotifications(changes);
        Assert.False(item.IsExpanded);

        changes.Clear();
        var replacement = first with { ArtifactUri = "alcoler-artifact://test/replacement.jpeg", MimeType = "image/jpeg", FileName = "replacement.jpeg" };
        item.UpdateFrom(complete with { Output = [replacement] });
        Assert.Same(replacement, Assert.Single(item.ImageMedia));
        Assert.Equal(string.Empty, item.OutputText);
        AssertImageNotifications(changes);

        changes.Clear();
        item.UpdateFrom(complete with { Output = [replacement with { }] });
        Assert.DoesNotContain(nameof(ToolCallItemViewModel.ImageMedia), changes);
        Assert.DoesNotContain(nameof(ToolCallItemViewModel.HasImages), changes);

        changes.Clear();
        item.UpdateFrom(complete with { Output = [new ToolResultText("text only"), new ToolResultMedia("file", "application/pdf")] });
        Assert.Empty(item.ImageMedia);
        Assert.False(item.HasImages);
        Assert.Equal("text only", item.OutputText);
        AssertImageNotifications(changes);

        item.UpdateFrom(complete);
        changes.Clear();
        item.UpdateFrom(pending);
        Assert.Empty(item.ImageMedia);
        Assert.False(item.HasImages);
        Assert.Equal(string.Empty, item.OutputText);
        AssertImageNotifications(changes);

        item.UpdateFrom(complete);
        changes.Clear();
        item.UpdateFrom(complete with { Output = [] });
        Assert.Empty(item.ImageMedia);
        Assert.False(item.HasImages);
        AssertImageNotifications(changes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConversationUpdates_ReplaceAndRemoveRowsWithoutLeakingImages(bool reset)
    {
        var (viewModel, store) = CreateConversation();
        using (store)
        using (viewModel)
        {
            var image = new ToolResultMedia("alcoler-artifact://test/first.png", "image/png", "first.png");
            var row = ToolRow() with { Status = ToolCallStatus.Succeeded, Output = [image] };
            ApplyRow(viewModel, 1, row, reset);
            var original = Assert.IsType<ToolCallItemViewModel>(Assert.Single(viewModel.Items));
            Assert.True(original.HasImages);

            var replacement = row with { Id = StrongId.NewToolCallId(), Output = null };
            ApplyRow(viewModel, 2, replacement, reset);
            var replaced = Assert.IsType<ToolCallItemViewModel>(Assert.Single(viewModel.Items));
            Assert.NotSame(original, replaced);
            Assert.Empty(replaced.ImageMedia);
            Assert.False(replaced.HasImages);

            ApplyRow(viewModel, 3, replacement with { Output = [image] }, reset);
            Assert.Same(replaced, Assert.Single(viewModel.Items));
            Assert.True(replaced.HasImages);
            viewModel.ApplyUpdate(new ProjectionUpdate(4, TestSnapshots.State(),
                reset ? [] : [new RowRemoved(row.RowId)], Reset: reset, ResetRows: reset ? [] : null));
            Assert.Empty(viewModel.Items);

            ApplyRow(viewModel, 5, replacement, reset);
            var restored = Assert.IsType<ToolCallItemViewModel>(Assert.Single(viewModel.Items));
            Assert.NotSame(replaced, restored);
            Assert.Empty(restored.ImageMedia);
            Assert.False(restored.HasImages);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Timeline_ShowsImagesOutsideCollapsedDetailsAndTracksUpdates(bool reset)
    {
        EnsureFluentTheme();
        var (viewModel, store) = CreateConversation();
        using (store)
        using (viewModel)
        {
            var view = new TimelineView { DataContext = viewModel };
            var window = new Window { Width = 420, Height = 600, Content = view };
            window.Show();
            try
            {
                var pending = ToolRow();
                ApplyRow(viewModel, 1, pending, reset);
                Pump(window);
                var item = Assert.IsType<ToolCallItemViewModel>(Assert.Single(viewModel.Items));
                Assert.Empty(view.GetVisualDescendants().OfType<ConversationImage>());
                var strip = Assert.Single(view.GetVisualDescendants().OfType<ItemsControl>(), control => control.Classes.Contains("toolImages"));
                Assert.False(strip.IsVisible);

                var first = new ToolResultMedia("alcoler-artifact://test/first.png", "image/png", "first.png");
                var second = new ToolResultMedia("alcoler-artifact://test/second.jpeg", "image/jpeg", "second.jpeg");
                var completed = pending with
                {
                    Status = ToolCallStatus.Succeeded,
                    Output = [first, new ToolResultText("image output text"), new ToolResultMedia("file", "application/pdf"), second],
                };
                ApplyRow(viewModel, 2, completed, reset);
                Pump(window);

                Assert.Same(item, Assert.Single(viewModel.Items));
                Assert.False(item.IsExpanded);
                Assert.True(strip.IsEffectivelyVisible);
                Assert.Collection(view.GetVisualDescendants().OfType<ConversationImage>(),
                    image => AssertThumbnail(image, first), image => AssertThumbnail(image, second));
                var rowPanel = Assert.IsType<StackPanel>(strip.Parent);
                var header = Assert.Single(rowPanel.Children.OfType<Button>(), button => button.Classes.Contains("toolRow"));
                var details = Assert.Single(rowPanel.Children.OfType<Border>(), border => border.Classes.Contains("toolDetails"));
                Assert.True(rowPanel.Children.IndexOf(header) < rowPanel.Children.IndexOf(strip));
                Assert.True(rowPanel.Children.IndexOf(strip) < rowPanel.Children.IndexOf(details));
                Assert.False(details.IsEffectivelyVisible);
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<SelectableTextBlock>(),
                    text => text.IsEffectivelyVisible && text.Text == "image output text");

                item.IsExpanded = true;
                Pump(window);
                Assert.True(strip.IsEffectivelyVisible);
                Assert.True(details.IsEffectivelyVisible);
                Assert.Contains(view.GetVisualDescendants().OfType<SelectableTextBlock>(),
                    text => text.IsEffectivelyVisible && text.Text == "image output text");
                Assert.True(strip.TranslatePoint(default, rowPanel)!.Value.Y + strip.Bounds.Height <=
                    details.TranslatePoint(default, rowPanel)!.Value.Y);
                item.IsExpanded = false;

                var replacement = first with { ArtifactUri = "alcoler-artifact://test/replacement.webp", MimeType = "image/webp", FileName = "replacement.webp" };
                ApplyRow(viewModel, 3, completed with { Output = [replacement] }, reset);
                Pump(window);
                AssertThumbnail(Assert.Single(view.GetVisualDescendants().OfType<ConversationImage>()), replacement);
                Assert.False(details.IsEffectivelyVisible);

                ApplyRow(viewModel, 4, completed with { Output = [new ToolResultText("plain output"), new ToolResultMedia("file", "video/mp4")] }, reset);
                Pump(window);
                Assert.False(strip.IsVisible);
                Assert.Empty(view.GetVisualDescendants().OfType<ConversationImage>());
                Assert.Equal("plain output", item.OutputText);

                ApplyRow(viewModel, 5, completed, reset);
                Pump(window);
                Assert.Equal(2, view.GetVisualDescendants().OfType<ConversationImage>().Count());
                ApplyRow(viewModel, 6, pending, reset);
                Pump(window);
                Assert.False(strip.IsVisible);
                Assert.Empty(view.GetVisualDescendants().OfType<ConversationImage>());
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void Timeline_NonImageMediaDoesNotCreateThumbnails()
    {
        EnsureFluentTheme();
        var (viewModel, store) = CreateConversation();
        using (store)
        using (viewModel)
        {
            var view = new TimelineView { DataContext = viewModel };
            var window = new Window { Width = 420, Height = 600, Content = view };
            window.Show();
            try
            {
                ApplyRow(viewModel, 1, ToolRow() with
                {
                    Status = ToolCallStatus.Succeeded,
                    Output = [new ToolResultText("plain output"), new ToolResultMedia("file", "application/pdf")],
                }, reset: false);
                Pump(window);

                Assert.Empty(view.GetVisualDescendants().OfType<ConversationImage>());
                Assert.False(Assert.Single(view.GetVisualDescendants().OfType<ItemsControl>(), control => control.Classes.Contains("toolImages")).IsVisible);
                Assert.Equal("plain output", Assert.IsType<ToolCallItemViewModel>(Assert.Single(viewModel.Items)).OutputText);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaTheory]
    [InlineData(420)]
    [InlineData(900)]
    public async Task Timeline_OffscreenImageUpdatesInvalidateHeightAndMatchWrappedStrip(double width)
    {
        EnsureFluentTheme();
        var (viewModel, store) = CreateConversation();
        using (store)
        using (viewModel)
        {
            var view = new TimelineView { DataContext = viewModel };
            var window = new Window { Width = width, Height = 400, Content = view };
            window.Show();
            try
            {
                var rows = Enumerable.Range(0, 80).Select(index => (ConversationRow)(ToolRow() with
                {
                    RowId = $"read-{index}",
                    Status = ToolCallStatus.Succeeded,
                })).ToArray();
                viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(), [], Reset: true, ResetRows: rows));
                await RenderAsync(window);
                view.Scroller.ScrollToHome();
                await RenderAsync(window);
                view.Scroller.ScrollToEnd();
                await RenderAsync(window);
                var panel = Assert.Single(view.GetVisualDescendants().OfType<ConversationPanel>());
                var item = Assert.IsType<ToolCallItemViewModel>(viewModel.Items[0]);
                Assert.DoesNotContain(panel.Children, child => ReferenceEquals(child.DataContext, item));
                var baseline = view.Scroller.Extent.Height;
                var images = Enumerable.Range(0, 4).Select(index => new ToolResultMedia($"alcoler-artifact://test/{index}.png", "image/png", $"{index}.png")).ToArray();
                var imageRow = ((ToolCallRow)rows[0]) with { Output = images };
                ApplyRow(viewModel, 2, imageRow, reset: false);
                await RenderAsync(window);

                var imagesPerRow = Math.Max(1, Math.Floor((panel.Bounds.Width - 24) / 134));
                var imageHeight = 12 + Math.Ceiling(images.Length / imagesPerRow) * 100;
                Assert.InRange(view.Scroller.Extent.Height, baseline + imageHeight - 1, baseline + imageHeight + 1);
                Assert.DoesNotContain(panel.Children, child => ReferenceEquals(child.DataContext, item));
                var estimatedExtent = view.Scroller.Extent.Height;
                view.Scroller.ScrollToHome();
                await RenderAsync(window);
                Assert.InRange(view.Scroller.Extent.Height, estimatedExtent - 1, estimatedExtent + 1);
                var strip = Assert.Single(view.GetVisualDescendants().OfType<ItemsControl>(),
                    control => control.Classes.Contains("toolImages") && ReferenceEquals(control.DataContext, item));
                Assert.Equal(4, strip.GetVisualDescendants().OfType<ConversationImage>().Count());
                Assert.Equal(imageHeight, strip.Bounds.Height + strip.Margin.Top + strip.Margin.Bottom);

                view.Scroller.ScrollToEnd();
                await RenderAsync(window);
                Assert.DoesNotContain(panel.Children, child => ReferenceEquals(child.DataContext, item));
                ApplyRow(viewModel, 3, imageRow with { Output = [images[0]] }, reset: false);
                await RenderAsync(window);
                Assert.InRange(view.Scroller.Extent.Height, baseline + 111, baseline + 113);
                view.Scroller.ScrollToHome();
                await RenderAsync(window);
                Assert.InRange(view.Scroller.Extent.Height, baseline + 111, baseline + 113);

                view.Scroller.ScrollToEnd();
                await RenderAsync(window);
                Assert.DoesNotContain(panel.Children, child => ReferenceEquals(child.DataContext, item));
                ApplyRow(viewModel, 4, imageRow with { Output = null }, reset: false);
                await RenderAsync(window);
                Assert.InRange(view.Scroller.Extent.Height, baseline - 1, baseline + 1);
                view.Scroller.ScrollToHome();
                await RenderAsync(window);
                Assert.Empty(view.GetVisualDescendants().OfType<ConversationImage>());
                Assert.InRange(view.Scroller.Extent.Height, baseline - 1, baseline + 1);
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static async Task RenderAsync(Window window)
    {
        Pump(window);
        var rendered = ElementComposition.GetElementVisual(window)!.Compositor.RequestCompositionBatchCommitAsync().Rendered;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await rendered.WaitAsync(TimeSpan.FromSeconds(5));
        Pump(window);
    }

    private static void AssertImageNotifications(IReadOnlyList<string?> changes)
    {
        Assert.Contains(nameof(ToolCallItemViewModel.ImageMedia), changes);
        Assert.Contains(nameof(ToolCallItemViewModel.HasImages), changes);
    }

    private static void AssertThumbnail(ConversationImage thumbnail, ToolResultMedia expected)
    {
        Assert.Equal(expected.ArtifactUri, thumbnail.ArtifactUri);
        Assert.Equal(expected.MimeType, thumbnail.MimeType);
        Assert.Equal(expected.FileName, thumbnail.FileName);
        Assert.True(thumbnail.IsEffectivelyVisible);
        Assert.True(thumbnail.Bounds.Width > 0);
        Assert.True(thumbnail.Bounds.Height > 0);
    }

    private static ToolCallRow ToolRow() => new("read-image", StrongId.NewToolCallId(), "Read",
        JsonSerializer.SerializeToElement(new { file_path = "image.png" }), ToolCallStatus.Pending,
        null, false, null, DateTimeOffset.UtcNow);

    private static (ConversationViewModel ViewModel, ProjectionStore Store) CreateConversation()
    {
        var runtime = new FakeAgentRuntime();
        var sessionId = StrongId.NewSessionId();
        var state = TestSnapshots.State();
        var projection = runtime.RegisterSession(sessionId, new ConversationSnapshot(0, 1, [], state));
        var store = new ProjectionStore(() => projection, sessionId, InlineUiScheduler.Instance, state);
        return (new ConversationViewModel(runtime, store), store);
    }

    private static void ApplyRow(ConversationViewModel viewModel, long sequence, ToolCallRow row, bool reset) =>
        viewModel.ApplyUpdate(new ProjectionUpdate(sequence, TestSnapshots.State(),
            reset ? [] : [new RowUpserted(row)], Reset: reset, ResetRows: reset ? [row] : null));

    private static void EnsureFluentTheme()
    {
        if (Application.Current is { } app && !app.Styles.OfType<FluentTheme>().Any())
            app.Styles.Add(new FluentTheme());
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
