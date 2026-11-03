using Avalonia.LogicalTree;
using Avalonia.Media;
using ColorTextBlock.Avalonia;
using ColorTextBlock.Avalonia.Geometries;

namespace Alcoler.Controls;

internal sealed class ConversationTextSpan(CTextBlock textBlock, IEnumerable<CInline> content) : CSpan(content)
{
    protected override IEnumerable<CGeometry> MeasureOverride(double entireWidth, double remainWidth)
    {
        foreach (var geometry in base.MeasureOverride(entireWidth, remainWidth))
            yield return geometry is DecoratorGeometry decorator ? new SelectionGeometry(decorator, textBlock) : geometry;
    }

    private sealed class SelectionGeometry : CGeometry
    {
        private readonly DecoratorGeometry _geometry;

        public SelectionGeometry(DecoratorGeometry geometry, CTextBlock textBlock)
            : base(geometry.Owner, geometry.Width, geometry.Height, geometry.BaseHeight,
                geometry.TextVerticalAlignment, geometry.LineBreak)
        {
            _geometry = geometry;
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

        public override void Render(DrawingContext context) => _geometry.Render(context);

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
