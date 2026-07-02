namespace Cursorial.UI.Bars;

/// <summary>Where a <see cref="Ribbon"/>'s Quick Access Toolbar sits (the bars guide's QAT placement toggle). Discrete
/// (whole-cell), like every bar density step.</summary>
public enum RibbonQuickAccessPlacement
{
    /// <summary>In the caption row above the tab strip (the default — the Office 2010+ resting position).</summary>
    AboveRibbon,

    /// <summary>In a band below the ribbon body (the "Show Below the Ribbon" option).</summary>
    BelowRibbon,
}
