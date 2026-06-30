namespace Cursorial.UI.Bars;

/// <summary>
/// The per-item overflow policy on a <see cref="Toolbar"/> (the Actipro <c>ToolBarItemOverflowBehavior</c> parity),
/// read by the <see cref="ToolbarOverflowPanel"/> when it folds the row. Set via
/// <see cref="Toolbar.SetOverflowMode"/> (the attached property) on a bar control.
/// </summary>
public enum ToolbarOverflowMode
{
    /// <summary>The item packs onto the row while it fits and moves into the overflow popup when it does not
    /// (the default — discrete, whole-control folding from the trailing edge).</summary>
    AsNeeded,

    /// <summary>The item is <b>always</b> in the overflow popup, never on the row — and its presence forces the
    /// overflow chevron to exist even when every <see cref="AsNeeded"/> item fits.</summary>
    Always,

    /// <summary>The item is <b>pinned</b> to the row and never overflows. If the pinned items overrun the bar they
    /// clip at the zone edge (a DEBUG diagnostic fires); they are charged against the row width unconditionally.</summary>
    Never
}
