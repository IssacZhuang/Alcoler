using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Alcoler.Controls;

public sealed class ConversationSelectableTextBlock : SelectableTextBlock
{
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Glyph advances can sum just beyond the measured width at a line boundary.
        var width = finalSize.Width > 0 ? finalSize.Width + LayoutHelper.LayoutEpsilon : finalSize.Width;
        base.ArrangeOverride(new Size(width, finalSize.Height));
        return finalSize;
    }
}
