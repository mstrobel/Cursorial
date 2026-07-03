using Cursorial.Drawing.Media;
using Cursorial.Rendering;
using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars;

/// <summary>
/// The bar separator (bars guide §3): a thin rule dividing related clusters on a bar surface. On a horizontal bar it
/// is an upright <c>│</c> (<see cref="Orientation.Vertical"/> — one cell wide, filling the row height); when the
/// <see cref="Toolbar"/> folds it into the vertical overflow popup it flips to a horizontal <c>─</c> rule spanning
/// the popup width (the <see cref="ToolbarOverflowPanel"/> sets <see cref="Separator.Orientation"/> by band). Derives
/// from the shared <see cref="Separator"/> for its <see cref="Separator.Orientation"/> property (and non-focusable
/// contract), but renders a glyph run rather than a <c>DrawLine</c> rule — a bar row is a single cell tall, where a
/// zero-length vertical line degenerates. The overflow trims leading/trailing separators from each band.
/// </summary>
public class BarSeparator : Separator
{
    static BarSeparator()
    {
        Control.ThemeProperty.OverrideDefaultValue<BarSeparator>(CursorialBarsTheme.SeparatorStyle());
        // Upright on the horizontal bar; the panel flips it to Horizontal when it lands in the vertical popup band.
        OrientationProperty.OverrideDefaultValue<BarSeparator>(Orientation.Vertical);
    }

    /// <inheritdoc/>
    // One cell on the rule axis; the cross axis fills via Stretch alignment (a horizontal rule fills the popup width,
    // a vertical rule fills the row height) — Render spans the arranged extent with the axis glyph.
    protected override Size MeasureOverride(Size availableSize) => new(1, 1);

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        var size = context.Size;
        if (size.Columns <= 0 || size.Rows <= 0 || Foreground is not { } brush)
            return;

        if (Orientation == Orientation.Vertical)
            for (var r = 0; r < size.Rows; r++)
                context.DrawText(0, r, "│", brush);
        else
            for (var c = 0; c < size.Columns; c++)
                context.DrawText(c, 0, "─", brush);
    }
}
