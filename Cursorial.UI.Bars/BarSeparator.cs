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
        // Upright on the horizontal bar; the panel flips it to Horizontal when it lands in the vertical popup band.
        OrientationProperty.OverrideDefaultValue<BarSeparator>(Orientation.Vertical);
    }

    /// <inheritdoc/>
    // One cell on the rule axis. An upright rule fills the band's AUTHORED height (1 on a toolbar or a Simplified
    // band, 2 beside a Large hero) — read from the band's stamp, never from ButtonSize: inside a RibbonControlGroup
    // the children wear Medium faces in a 2-row band, and under :layout-simplified a Large ButtonSize wears a 1-row
    // face. A horizontal rule stays 1×1 and fills the popup width via Stretch — Render spans the arranged extent.
    protected override Size MeasureOverride(Size availableSize) =>
        new(1, Orientation is Orientation.Vertical ? GetValue(Ribbon.BandContentRowsProperty) : 1);

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
