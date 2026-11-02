using Avalonia.Media;
using ColorTextBlock.Avalonia;
using ColorTextBlock.Avalonia.Geometries;

namespace Alcoler.Controls;

internal sealed class ConversationTextSpan(CTextBlock textBlock, IEnumerable<CInline> content) : CSpan(content)
{
    protected override IEnumerable<CGeometry> MeasureOverride(double entireWidth, double remainWidth)
    {
        foreach (var geometry in base.MeasureOverride(entireWidth, remainWidth))
            yield return geometry is DecoratorGeometry ? new EndInclusiveGeometry(geometry, textBlock) : geometry;
    }

    private sealed class EndInclusiveGeometry : CGeometry
    {
        private readonly CGeometry _geometry;

        public EndInclusiveGeometry(CGeometry geometry, CTextBlock textBlock)
            : base(geometry.Owner, geometry.Width, geometry.Height, geometry.BaseHeight,
                geometry.TextVerticalAlignment, geometry.LineBreak)
        {
            _geometry = geometry;
            OnMouseEnter = geometry.OnMouseEnter;
            OnMouseLeave = geometry.OnMouseLeave;
            OnMousePressed = geometry.OnMousePressed;
            OnMouseReleased = geometry.OnMouseReleased;
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

        public override TextPointer CalcuatePointerFrom(double x, double y) => _geometry.CalcuatePointerFrom(x, y);

        public override TextPointer GetBegin() => _geometry.GetBegin();

        public override TextPointer GetEnd() => _geometry.GetEnd();
    }
}
