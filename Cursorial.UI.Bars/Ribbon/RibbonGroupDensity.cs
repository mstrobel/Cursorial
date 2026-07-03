namespace Cursorial.UI.Bars;

/// <summary>
/// A <see cref="RibbonGroup"/>'s density tier under the band's width-driven fold (the design guide's discrete density
/// steps — "large icon-over-label buttons → small inline buttons → a single collapsed group dropdown. Three discrete
/// tiers, not a fluid slide"). The tier is <b>band-assigned</b>, not author-set (the <see cref="RibbonBand"/> owns the
/// shared width budget); an author only caps how far a group may demote via <c>Ribbon.MinDensity</c>. Ordered
/// shallowest → deepest so <c>tier &gt;= floor</c> reads as "at or past the demotion floor".
/// </summary>
public enum RibbonGroupDensity
{
    /// <summary>Full authored presentation: large glyph-over-label buttons beside small inline ones, the group name
    /// footer, the optional ⋰ launcher. The default — a group only leaves it under width pressure.</summary>
    Normal,

    /// <summary>Every hosted control drops to its small inline face (a group's <c>Large</c> buttons render as compact
    /// <c>[icon][label]</c>), reclaiming rows/cells while the whole group stays inline and keyboard-transparent.</summary>
    Compact,

    /// <summary>The whole group becomes a single dropdown button (group name + ▾); its controls move into the flyout
    /// (at authored size). The densest tier — one cell-cluster stands in for the group until the terminal widens.</summary>
    Collapsed,
}
