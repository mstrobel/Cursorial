namespace Cursorial.Core.Input;

/// <summary>
/// Describes which mouse interactions an input device reports.
/// </summary>
/// <param name="ButtonPress">Reports button-down events.</param>
/// <param name="ButtonRelease">Reports button-up events distinct from press.</param>
/// <param name="Drag">Reports motion events that occur with one or more buttons held.</param>
/// <param name="Motion">Reports motion events with no button held (any-event tracking).</param>
/// <param name="Wheel">Reports vertical and/or horizontal wheel deltas.</param>
/// <param name="PixelCoordinates">Reports positions in pixel units in addition to cell rows/columns.</param>
/// <param name="ExtendedButtonCount">
/// Number of distinct buttons reported beyond left/middle/right (0 if none). For example, a
/// device that reports X1 and X2 has an <c>ExtendedButtonCount</c> of 2.
/// </param>
public sealed record class MouseCapabilities(
    bool ButtonPress,
    bool ButtonRelease,
    bool Drag,
    bool Motion,
    bool Wheel,
    bool PixelCoordinates,
    int ExtendedButtonCount)
{
    public static MouseCapabilities None { get; } = new(
        ButtonPress: false,
        ButtonRelease: false,
        Drag: false,
        Motion: false,
        Wheel: false,
        PixelCoordinates: false,
        ExtendedButtonCount: 0);
}
