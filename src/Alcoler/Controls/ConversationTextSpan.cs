using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.LogicalTree;
using Avalonia.Media;
using ColorTextBlock.Avalonia;
using ColorTextBlock.Avalonia.Geometries;

namespace Alcoler.Controls;

internal sealed class ConversationTextSpan(CTextBlock textBlock, IEnumerable<CInline> content) : CSpan(content)
{
    protected override IEnumerable<CGeometry> MeasureOverride(double entireWidth, double remainWidth)
    {
        var offset = 0;
        foreach (var geometry in base.MeasureOverride(entireWidth, remainWidth))
        {
            yield return geometry is DecoratorGeometry decorator ? new SelectionGeometry(decorator, textBlock, offset) : geometry;
            offset += geometry.CaretLength;
        }
    }

    private sealed class SelectionGeometry : CGeometry
    {
        private readonly DecoratorGeometry _geometry;
        private readonly CTextBlock _textBlock;
        private readonly int _offset;

        public SelectionGeometry(DecoratorGeometry geometry, CTextBlock textBlock, int offset)
            : base(geometry.Owner, geometry.Width, geometry.Height, geometry.BaseHeight,
                geometry.TextVerticalAlignment, geometry.LineBreak)
        {
            _geometry = geometry;
            _textBlock = textBlock;
            _offset = offset;
            OnMouseEnter = geometry.OnMouseEnter;
            OnMouseLeave = geometry.OnMouseLeave;
            // Decorators expose forwarding callbacks even when plain code has no action.
            if (geometry.Owner is not CCode || geometry.Owner.GetLogicalAncestors().OfType<CHyperlink>().Any())
            {
                OnMousePressed = geometry.OnMousePressed;
                OnMouseReleased = geometry.OnMouseReleased;
            }
            OnClick = geometry.OnClick;
            geometry.RepaintRequested += textBlock.InvalidateVisual;
        }

        public override int CaretLength => _geometry.CaretLength;

        public override void Arranged()
        {
            _geometry.Left = Left;
            _geometry.Top = Top;
            _geometry.Arranged();
        }

        public override void Render(DrawingContext context)
        {
            _geometry.Render(context);
            if (_textBlock.Selection is not { } selection)
                return;
            RenderSelection(context, _geometry, _offset, Math.Min(selection.From, selection.To),
                Math.Max(selection.From, selection.To), _textBlock.SelectionBrush ?? Brushes.Cyan);
        }

        private static void RenderSelection(DrawingContext context, CGeometry geometry, int offset,
            int from, int to, IBrush brush)
        {
            var start = Math.Max(0, from - offset);
            var end = Math.Min(geometry.CaretLength, to - offset);
            if (start >= end)
                return;
            if (geometry is DecoratorGeometry decorator)
            {
                foreach (var target in decorator.Targets)
                {
                    RenderSelection(context, target, offset, from, to, brush);
                    offset += target.CaretLength;
                }
                return;
            }
            if (geometry.Owner is not CRun)
                return;

            var left = GetCaretDistance(geometry.CalcuatePointerFrom(start));
            var right = GetCaretDistance(end == geometry.CaretLength ? geometry.GetEnd() : geometry.CalcuatePointerFrom(end));
            var bounds = new Rect(geometry.Left + Math.Min(left, right), geometry.Top, Math.Abs(right - left), geometry.Height);
            var foreground = new DrawingGroup();
            using (var drawing = foreground.Open())
                geometry.Render(drawing);
            // Inline runs inherit the opaque code background.
            if (foreground.Children.FirstOrDefault() is GeometryDrawing { Brush: not null, Pen: null })
                foreground.Children.RemoveAt(0);
            using var clip = context.PushClip(bounds);
            context.FillRectangle(brush, bounds);
            foreground.Draw(context);
        }

        // ColorTextBlock.Avalonia 11.0.3 keeps measured caret distances internal.
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Distance")]
        private static extern double GetCaretDistance(TextPointer pointer);

        // Document selections include the end caret excluded by DecoratorGeometry.
        public override TextPointer CalcuatePointerFrom(int index) =>
            index == CaretLength ? GetEnd() : _geometry.CalcuatePointerFrom(index);

        public override TextPointer CalcuatePointerFrom(double x, double y) =>
            CalcuatePointerFrom(HitTestIndex(_geometry, x, y));

        private static int HitTestIndex(CGeometry geometry, double x, double y)
        {
            if (geometry is not DecoratorGeometry decorator)
                return geometry.CalcuatePointerFrom(x, y).Index;
            if (x < decorator.Left)
                return decorator.GetBegin().Index;

            var offset = 0;
            for (var i = 0; i < decorator.Targets.Length; i++)
            {
                var target = decorator.Targets[i];
                // Nested geometries share the text block's coordinate system.
                if (x <= target.Left + target.Width || i == decorator.Targets.Length - 1)
                    return offset + HitTestIndex(target, x, y);
                offset += target.CaretLength;
            }
            return decorator.GetEnd().Index;
        }

        public override TextPointer GetBegin() => _geometry.GetBegin();

        public override TextPointer GetEnd() => _geometry.GetEnd();
    }
}
