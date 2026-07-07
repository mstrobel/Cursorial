namespace Cursorial.UI.Bars;

/// <summary>
/// How a <see cref="RibbonControlGroup"/> decides its row count within the band (the consolidation of Actipro's
/// <c>RibbonControlGroup</c> stacking + <c>RibbonMultiRowControlGroup</c> two-row packing for a 1–2-row terminal
/// ribbon). Row count is decided from AUTHORED facts only (never density-demoted faces), so width folds re-ink faces
/// without ever re-flowing rows — a stack is already the narrowest form of its controls, and flattening would trade
/// height for MORE width.
/// </summary>
public enum RibbonControlGroupRowBehavior
{
    /// <summary>Stack into two rows when the band's authored height is 2 (some control on the band authors a
    /// <see cref="RibbonButtonSize.Large"/> face, or a sibling group pins <see cref="TwoRow"/>); lay out as a single
    /// flat row when the band is 1-row — so a control group never forces a tall band by itself. The default.</summary>
    Auto,

    /// <summary>Always a single flat row, even in a 2-row band (the controls center vertically like any other 1-row
    /// control). For a run that must read left-to-right regardless of band height.</summary>
    SingleRow,

    /// <summary>Always stack into two rows — an authored vote that MAKES the band 2 rows tall, for a dense tab with no
    /// large hero button (the "paired small-button columns" pattern without a Large anchor).</summary>
    TwoRow,
}
