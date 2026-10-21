using Alcoler.Workbench.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Assert = Xunit.Assert;

namespace Alcoler.Workbench.Tests.Views;

public class IconTests
{
    private static readonly string[] Paths =
    [
        "M5 12h14M12 5v14",
        "m9 18 6-6-6-6",
        "m6 9 6 6 6-6",
        "M5 12h14",
        "M5 12h.01M12 12h.01M19 12h.01",
        "M2 3 6 9",
    ];

    [AvaloniaTheory]
    [InlineData(14, 0.875)]
    [InlineData(16, 1)]
    [InlineData(24, 1.5)]
    [InlineData(32, 2)]
    [InlineData(48, 3)]
    public void Render_PreservesFixedViewportAndScalesStroke(double size, double expectedStroke)
    {
        foreach (var path in Paths)
        {
            var data = Geometry.Parse(path);
            var icon = new Icon { Data = data, Width = size, Height = size };
            icon.Measure(new Size(size, size));
            icon.Arrange(new Rect(0, 0, size, size));

            var recording = Record(icon);
            var viewport = Assert.IsType<DrawingGroup>(Assert.Single(recording.Children));
            var drawing = Assert.IsType<GeometryDrawing>(Assert.Single(viewport.Children));
            var transform = Assert.IsType<MatrixTransform>(viewport.Transform).Matrix;
            var scale = size / 24;
            Assert.Equal(new Matrix(scale, 0, 0, scale, 0, 0), transform);
            Assert.Equal(data.Bounds, drawing.Geometry!.Bounds);
            Assert.Null(data.Transform);
            var pen = Assert.IsType<Pen>(drawing.Pen);
            Assert.Equal(1.5, pen.Thickness);
            Assert.Equal(expectedStroke, pen.Thickness * transform.M11, 6);
            Assert.Equal(PenLineCap.Round, pen.LineCap);
            Assert.Equal(PenLineJoin.Round, pen.LineJoin);
        }
    }

    [AvaloniaTheory]
    [InlineData(32, 16, 8, 0)]
    [InlineData(16, 32, 0, 8)]
    public void Render_CentersViewportInNonSquareBounds(double width, double height, double x, double y)
    {
        var icon = new Icon { Data = Geometry.Parse("M5 12h14"), Width = width, Height = height };
        icon.Measure(new Size(width, height));
        icon.Arrange(new Rect(0, 0, width, height));

        var viewport = Assert.IsType<DrawingGroup>(Assert.Single(Record(icon).Children));
        var transform = Assert.IsType<MatrixTransform>(viewport.Transform).Matrix;
        Assert.Equal(new Matrix(16d / 24, 0, 0, 16d / 24, x, y), transform);
        var center = new Point(12, 12).Transform(transform);
        Assert.Equal(width / 2, center.X, 6);
        Assert.Equal(height / 2, center.Y, 6);
    }

    [AvaloniaFact]
    public void Render_ChevronAndHorizontalLineKeepTheirLucidePlacement()
    {
        var chevron = RenderedGeometryBounds("m9 18 6-6-6-6", 16);
        Assert.Equal(6, chevron.X, 6);
        Assert.Equal(4, chevron.Y, 6);
        Assert.Equal(4, chevron.Width, 6);
        Assert.Equal(8, chevron.Height, 6);
        Assert.Equal(new Point(8, 8), chevron.Center);

        var line = RenderedGeometryBounds("M5 12h14", 16);
        Assert.Equal(16d * 5 / 24, line.X, 6);
        Assert.Equal(8, line.Y, 6);
        Assert.Equal(16d * 14 / 24, line.Width, 6);
        Assert.Equal(0, line.Height);
        Assert.Equal(8, line.Center.X, 6);
        Assert.Equal(8, line.Center.Y, 6);
    }

    [AvaloniaFact]
    public void Measure_DoesNotDependOnGeometryBounds()
    {
        foreach (var path in Paths)
        {
            var icon = new Icon { Data = Geometry.Parse(path) };
            icon.Measure(Size.Infinity);
            Assert.Equal(new Size(24, 24), icon.DesiredSize);
        }
    }

    [AvaloniaFact]
    public void Render_PreservesFillAndCustomStroke()
    {
        var icon = new Icon
        {
            Data = Geometry.Parse("M5 5h14v14H5Z"),
            Fill = Brushes.Red,
            Stroke = Brushes.Blue,
            StrokeThickness = 3,
        };
        icon.Measure(new Size(24, 24));
        icon.Arrange(new Rect(0, 0, 24, 24));
        var viewport = Assert.IsType<DrawingGroup>(Assert.Single(Record(icon).Children));
        var drawing = Assert.IsType<GeometryDrawing>(Assert.Single(viewport.Children));
        Assert.Same(icon.Fill, drawing.Brush);
        var pen = Assert.IsType<Pen>(drawing.Pen);
        Assert.Same(icon.Stroke, pen.Brush);
        Assert.Equal(3, pen.Thickness);
    }

    [AvaloniaFact]
    public void Render_SkipsMissingGeometryAndEmptyBounds()
    {
        var icon = new Icon();
        icon.Measure(new Size(24, 24));
        icon.Arrange(new Rect(0, 0, 24, 24));
        Assert.Empty(Record(icon).Children);

        icon = new Icon { Data = Geometry.Parse("M5 12h14"), Width = 0, Height = 0 };
        icon.Measure(default);
        icon.Arrange(default);
        Assert.Empty(Record(icon).Children);
    }

    [AvaloniaFact]
    public void Styles_SizeIconsAndCenterAdjacentText()
    {
        var icon = new Icon { Data = Geometry.Parse("M5 12h14M12 5v14") };
        icon.Classes.Add("icon");
        var small = new Icon { Data = Geometry.Parse("m6 9 6 6 6-6") };
        small.Classes.Add("icon");
        small.Classes.Add("small");
        var label = new TextBlock { Text = "Add provider", FontSize = 14 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Height = 36 };
        row.Children.Add(icon);
        row.Children.Add(label);
        row.Children.Add(small);
        var button = new Button { Content = row };
        button.Classes.Add("primary");
        var window = new Window { Width = 400, Height = 120, Content = button };
        window.Show();
        try
        {
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = theme;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.Equal(new Size(16, 16), icon.Bounds.Size);
                Assert.Equal(new Size(14, 14), small.Bounds.Size);
                Assert.Equal(VerticalAlignment.Center, label.VerticalAlignment);
                Assert.Equal(row.Bounds.Height / 2, icon.Bounds.Center.Y, 6);
                Assert.Equal(icon.Bounds.Center.Y, small.Bounds.Center.Y, 6);
                Assert.InRange(Math.Abs(icon.Bounds.Center.Y - label.Bounds.Center.Y), 0, 0.5 / window.RenderScaling);
                Assert.True(window.TryFindResource("AlcolerBrandText", theme, out var foreground));
                var color = Assert.IsAssignableFrom<ISolidColorBrush>(foreground).Color;
                Assert.Equal(color, Assert.IsAssignableFrom<ISolidColorBrush>(icon.Stroke).Color);
                Assert.Equal(color, Assert.IsAssignableFrom<ISolidColorBrush>(small.Stroke).Color);
                Assert.Equal(color, Assert.IsAssignableFrom<ISolidColorBrush>(label.Foreground).Color);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Settings_ProviderActionsKeepsVisibleFillAcrossThemes()
    {
        var dialog = new Workbench.Views.SettingsDialog(new WorkbenchConfig(
        [
            new("openai-compatible", "https://example.invalid/v1", null, [new("test-model")]),
        ]));
        dialog.Show();
        try
        {
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                dialog.RequestedThemeVariant = theme;
                Dispatcher.UIThread.RunJobs();
                dialog.UpdateLayout();
                var button = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(),
                    candidate => Avalonia.Automation.AutomationProperties.GetName(candidate) == "Provider actions");
                var icon = Assert.IsType<Icon>(button.Content);
                Assert.True(icon.IsEffectivelyVisible);
                Assert.Equal(new Size(16, 16), icon.Bounds.Size);
                Assert.Equal(0, icon.StrokeThickness);
                Assert.True(dialog.TryFindResource("AlcolerTextSecondary", theme, out var expected));
                var color = Assert.IsAssignableFrom<ISolidColorBrush>(icon.Fill).Color;
                Assert.True(color.A > 0);
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(expected).Color, color);
                var viewport = Assert.IsType<DrawingGroup>(Assert.Single(Record(icon).Children));
                var drawing = Assert.IsType<GeometryDrawing>(Assert.Single(viewport.Children));
                Assert.Null(drawing.Pen);
                Assert.Equal(color, Assert.IsAssignableFrom<ISolidColorBrush>(drawing.Brush).Color);
            }
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void Shell_UsesNativeIconsInsteadOfBoundsStretchedPaths()
    {
        using var viewModel = ShellSmokeTests.CreateWorkbench();
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var descendants = window.GetVisualDescendants().ToArray();
            Assert.NotEmpty(descendants.OfType<Icon>());
            Assert.DoesNotContain(descendants.OfType<Shape>(), shape => shape.Classes.Contains("icon"));
            foreach (var icon in descendants.OfType<Icon>().Where(icon => icon.IsEffectivelyVisible))
            {
                var expectedSize = icon.Classes.Contains("small") ? 14 : 16;
                Assert.Equal(expectedSize, icon.Width);
                Assert.Equal(expectedSize, icon.Height);
                Assert.Equal(1.5, icon.StrokeThickness);
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static Rect RenderedGeometryBounds(string path, double size)
    {
        var icon = new Icon { Data = Geometry.Parse(path), Width = size, Height = size };
        icon.Measure(new Size(size, size));
        icon.Arrange(new Rect(0, 0, size, size));
        var viewport = Assert.IsType<DrawingGroup>(Assert.Single(Record(icon).Children));
        var geometry = Assert.IsType<GeometryDrawing>(Assert.Single(viewport.Children)).Geometry!;
        return geometry.Bounds.TransformToAABB(viewport.Transform!.Value);
    }

    private static DrawingGroup Record(Icon icon)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            icon.Render(context);
        }
        return drawing;
    }
}
