namespace Cursorial.UI;

/// <summary>
/// The element visibility states (design doc §5.1). Routing is custom (LD5): a flip into or out of
/// <see cref="Collapsed"/> invalidates measure on self and parent (layout space is released or
/// reclaimed); a <see cref="Visible"/> ↔ <see cref="Hidden"/> flip is render-side only — a Hidden
/// element keeps its layout slot but paints nothing and is not hit-testable.
/// </summary>
public enum Visibility : byte
{
    /// <summary>The element participates in layout, renders, and hit-tests.</summary>
    Visible,

    /// <summary>The element occupies layout space but paints nothing and is not hit-testable.</summary>
    Hidden,

    /// <summary>The element occupies no layout space (desired size 0×0, empty bounds) and paints nothing.</summary>
    Collapsed,
}

/// <summary>Horizontal placement of an element within its layout slot (design doc §5.1; offsets per matrix LD3).</summary>
public enum HorizontalAlignment : byte
{
    /// <summary>Fill the slot; when explicit/Max sizing prevents filling, the element centers (LD3).</summary>
    Stretch,

    /// <summary>Pin to the slot's left edge.</summary>
    Left,

    /// <summary>Center within the slot (floor — the spare cell goes right).</summary>
    Center,

    /// <summary>Pin to the slot's right edge (offset clamps ≥ 0 — overflow pins left).</summary>
    Right,
}

/// <summary>Vertical placement of an element within its layout slot (design doc §5.1; offsets per matrix LD3).</summary>
public enum VerticalAlignment : byte
{
    /// <summary>Fill the slot; when explicit/Max sizing prevents filling, the element centers (LD3).</summary>
    Stretch,

    /// <summary>Pin to the slot's top edge.</summary>
    Top,

    /// <summary>Center within the slot (floor — the spare cell goes bottom).</summary>
    Center,

    /// <summary>Pin to the slot's bottom edge (offset clamps ≥ 0 — overflow pins top).</summary>
    Bottom,
}

/// <summary>
/// Layout flow direction. Owned by S1 (design doc §5.1); panels (<see cref="StackPanel"/>,
/// <c>WrapPanel</c>) and S8 controls cite this single definition.
/// </summary>
public enum Orientation : byte
{
    /// <summary>Lay out along the column (horizontal) axis.</summary>
    Horizontal,

    /// <summary>Lay out along the row (vertical) axis.</summary>
    Vertical,
}

/// <summary>The edge a <see cref="DockPanel"/> child docks against (<see cref="DockPanel.DockProperty"/>).</summary>
public enum Dock : byte
{
    /// <summary>Dock against the left edge (the attached property's default).</summary>
    Left,

    /// <summary>Dock against the top edge.</summary>
    Top,

    /// <summary>Dock against the right edge.</summary>
    Right,

    /// <summary>Dock against the bottom edge.</summary>
    Bottom,
}
