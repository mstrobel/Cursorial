namespace Cursorial.UI.Bars.Input;

/// <summary>
/// What a <see cref="KeyTipEntry"/> does when its badge letter is fully typed (keytips-design §5, graft G3).
/// The FSM treats every <c>Drill*</c> kind uniformly (reveal → park → push the next level); the distinct kinds
/// exist for diagnostics and to document the reveal each closure encapsulates.
/// </summary>
public enum KeyTipTargetKind
{
    /// <summary>A leaf: invoke the target (its <c>Click</c>/command), then exit the overlay.</summary>
    Activate,

    /// <summary>Select a ribbon tab (float a minimized band), then push the tab's group level.</summary>
    DrillTab,

    /// <summary>Enter a ribbon group (the band is already shown), then push the group's control level.</summary>
    DrillGroup,

    /// <summary>Open a dropdown / collapsed-group flyout, then push a level over the opened surface's items.</summary>
    DrillPopup,
}
