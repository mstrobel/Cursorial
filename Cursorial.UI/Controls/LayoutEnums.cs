namespace Cursorial.UI.Controls;

/// <summary>
/// Layout flow direction. Owned by S1 (design doc §5.1); panels (<see cref="StackPanel"/>,
/// <c>WrapPanel</c>) and S8 controls cite this single definition.
/// </summary>
public enum Orientation : byte
{
    /// <summary>Lay out along the column (horizontal) axis.</summary>
    Horizontal,

    /// <summary>Lay out along the row (vertical) axis.</summary>
    Vertical
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
    Bottom
}