using Cursorial.Drawing.Media;
using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// A non-interactive divider between menu items / toolbar groups (design doc §12.7). Not focusable and not a tab
/// stop; in a menu it renders as a horizontal rule that junction-merges into the menu's side borders. It is its
/// own container in an <see cref="ItemsControl"/> (an item that IS a <see cref="Separator"/> is used directly).
/// </summary>
public class Separator : Control
{
    /// <summary>Creates a separator (never focusable, never a tab stop).</summary>
    public Separator()
    {
        Focusable = false;
        IsTabStop = false;
    }
    
    static Separator()
    {
        PseudoClassMapping.Register<Separator>(MenuItem.IsWithinMenuProperty, ":within-menu");
    }

    protected override void Render(RenderContext context)
    {
        base.Render(context);

        if (BorderPen is not {} pen)
            return;

        context.DrawLine(0, 0, context.Size.Columns - 1, 0, pen, overwrite: true);
    }
}
