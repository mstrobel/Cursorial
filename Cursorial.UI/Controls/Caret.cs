using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// A zero-size overlay that publishes the <b>real terminal caret</b> (a blinking underline by default)
/// at its arranged origin while <see cref="IsCaretShown"/> is set — a focus / text-edit indicator. It
/// paints nothing: the caret IS the terminal cursor, so its blink is terminal-native (zero re-raster)
/// and it carries terminal-level semantics for assistive technology (design doc §5.9, the one v1
/// accessibility affordance). Place it where the caret should appear — e.g. inside a
/// <see cref="CheckBox"/>'s <c>[ ]</c> box, or in a future <c>TextBox</c> at the insertion point — and
/// drive <see cref="IsCaretShown"/> (typically from a <c>:focus-visible</c> style rule). Publication is
/// clip-gated and dropped on detach by <see cref="ITerminalCaretService"/>, so a caret scrolled out of
/// view or on a hidden/detached subtree never strands a terminal cursor.
/// </summary>
public sealed class Caret : UIElement
{
    /// <summary>Whether the caret is currently published (the terminal cursor shows at this element's origin).</summary>
    public static readonly StyledProperty<bool> IsCaretShownProperty =
        UIProperty.Register<Caret, bool>(nameof(IsCaretShown), changed: OnShownChanged);

    /// <summary>The caret's <see cref="CursorShape"/> (default <see cref="CursorShape.BlinkingUnderline"/>).</summary>
    public static readonly StyledProperty<CursorShape> CaretShapeProperty =
        UIProperty.Register<Caret, CursorShape>(nameof(CaretShape), defaultValue: CursorShape.BlinkingUnderline, changed: OnShapeChanged);

    /// <inheritdoc cref="IsCaretShownProperty"/>
    public bool IsCaretShown { get => GetValue(IsCaretShownProperty); set => SetValue(IsCaretShownProperty, value); }

    /// <inheritdoc cref="CaretShapeProperty"/>
    public CursorShape CaretShape { get => GetValue(CaretShapeProperty); set => SetValue(CaretShapeProperty, value); }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize) => Size.Empty; // zero-size overlay

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize) => Size.Empty; // paints nothing; only its origin matters

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        UpdatePublication();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        // The service also drops stale (detached) owners at assembly, but withdraw eagerly so a
        // hide-then-detach can't briefly republish.
        UIApplication.Current?.CaretService.Clear(this);
        base.OnDetachedFromTree(in e);
    }

    private static void OnShownChanged(UIObject sender, bool oldValue, bool newValue) => ((Caret)sender).UpdatePublication();

    private static void OnShapeChanged(UIObject sender, CursorShape oldValue, CursorShape newValue) => ((Caret)sender).UpdatePublication();

    // Publish at element-local (0, 0): the service transforms to window coordinates live at frame
    // assembly (render/scroll offsets + clip folded), so the caret tracks scrolling/layout without
    // re-publishing. Equality-gated in the service, so a redundant call is a no-op.
    private void UpdatePublication()
    {
        if (UIApplication.Current?.CaretService is not { } service)
            return;

        if (IsCaretShown && IsAttachedToTree)
            service.Publish(this, 0, 0, CaretShape);
        else
            service.Clear(this);
    }
}
