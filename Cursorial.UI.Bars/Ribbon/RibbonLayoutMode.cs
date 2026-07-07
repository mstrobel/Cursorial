namespace Cursorial.UI.Bars;

/// <summary>
/// The <see cref="Ribbon"/>'s overall layout density — the USER-directed axis (bind it to a command or a user-options
/// key), distinct from the band's width-driven fold, which stays width-reactive within whatever mode is active. The
/// three values form a progression of shrinking vertical affordance: full ribbon → one labeled row → one icon row.
/// </summary>
public enum RibbonLayoutMode
{
    /// <summary>The full authored presentation (the Office "classic ribbon"): a 1- or 2-row band per the authored
    /// faces (<see cref="RibbonButtonSize.Large"/> heroes beside stacked <see cref="RibbonControlGroup"/> columns),
    /// group-name footers, dialog launchers. The default.</summary>
    Classic,

    /// <summary>The Office "simplified ribbon": a single-row band — <see cref="RibbonButtonSize.Large"/> faces demote
    /// to their <c>[icon][label]</c> row (authored sizes untouched — flip back and the classic faces restore exactly),
    /// <see cref="RibbonControlGroup"/>s lay flat, and the group-name footer row disappears. Width pressure is
    /// relieved by the ordinary density fold (labels drop, then groups collapse to dropdowns).</summary>
    Simplified,

    /// <summary>Everything renders at its smallest face, always: <see cref="Simplified"/>'s single row PLUS the
    /// ribbon-wide compact signal (icon-only wherever an icon exists; a label-only control keeps its label). The
    /// densest stable form — under width pressure the fold goes straight to collapsing groups.</summary>
    Compact,
}
