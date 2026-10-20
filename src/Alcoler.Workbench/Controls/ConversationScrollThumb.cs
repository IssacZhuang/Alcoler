using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Alcoler.Workbench.Controls;

public sealed class ConversationScrollThumb : Thumb
{
    protected override void OnDragDelta(VectorEventArgs e)
    {
        // Avalonia's Track divides by the vertical delta when adjusting the drag origin.
        if (e.Vector.Y == 0 || !double.IsFinite(e.Vector.X) || !double.IsFinite(e.Vector.Y))
            e.Handled = true;
        else
            base.OnDragDelta(e);
    }
}
