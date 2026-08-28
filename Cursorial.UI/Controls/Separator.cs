using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// A non-interactive divider between menu items / toolbar groups (design doc §12.7). Not focusable and not a tab
/// stop; in a menu it renders as a horizontal rule that junction-merges into the menu's side borders. It is its
/// own container in an <see cref="ItemsControl"/> (an item that IS a <see cref="Separator"/> is used directly).
/// </summary>
public class Separator : Control
{
    /// <summary>The rule's axis (default <see cref="Controls.Orientation.Horizontal"/> — a rule across the width, for
    /// vertical menus/lists). A <see cref="StatusBar"/> sets its separators <see cref="Controls.Orientation.Vertical"/>
    /// (a rule down the height) so they read as upright dividers between status cells.</summary>
    public static readonly StyledProperty<Orientation> OrientationProperty =
        UIProperty.Register<Separator, Orientation>(nameof(Orientation), defaultValue: Orientation.Horizontal);

    /// <summary>Creates a separator (never focusable, never a tab stop).</summary>
    public Separator()
    {
        Focusable = false;
        IsTabStop = false;
    }

    static Separator()
    {
        PseudoClassMapping.Register<Separator>(MenuItem.IsWithinMenuProperty, ":within-menu");
        AffectsRender<Separator>(OrientationProperty);
    }

    /// <inheritdoc cref="OrientationProperty"/>
    public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Template is not null)
            return base.MeasureOverride(availableSize);

        return new Size(1, 1).ClampTo(availableSize);
    }

    protected override void Render(RenderContext context)
    {
        var size = context.Size;
        if (size.Columns <= 0 || size.Rows <= 0)
            return; // degenerate (e.g. a 0-width separator docked in a horizontal bar) — nothing to draw, never a -1 Rect

        base.Render(context);

        if (BorderPen is not {} pen)
            return;

        if (Orientation == Orientation.Vertical)
            context.DrawLine(0, 0, 0, size.Rows - 1, pen, overwrite: true);
        else
            context.DrawLine(0, 0, size.Columns - 1, 0, pen, overwrite: true);
    }
}
