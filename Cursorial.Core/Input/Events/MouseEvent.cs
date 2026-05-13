namespace Cursorial.Input.Events;

/// <summary>
/// A mouse event reporting button activity, motion, drag, or wheel deltas.
/// </summary>
public sealed record MouseEvent : InputEvent
{
    /// <summary>What kind of mouse activity this event represents.</summary>
    public required MouseEventKind Kind { get; init; }

    /// <summary>The mouse position in cell rows/columns and (when available) pixels.</summary>
    public required CellPosition Position { get; init; }

    /// <summary>The button associated with this event, or <see cref="MouseButton.None"/> for motion-only.</summary>
    public required MouseButton Button { get; init; }

    /// <summary>Bitmask of all buttons currently held at the time of the event.</summary>
    public required MouseButtons ButtonsHeld { get; init; }

    /// <summary>Modifier keys held when the event occurred.</summary>
    public required KeyModifiers Modifiers { get; init; }

    /// <summary>
    /// Vertical wheel delta in 1/120 notch units (positive = away from user). Zero when the
    /// event is not a <see cref="MouseEventKind.Wheel"/> event.
    /// </summary>
    public int WheelDeltaY { get; init; }

    /// <summary>
    /// Horizontal wheel delta in 1/120 notch units (positive = right). Zero when the event is
    /// not a <see cref="MouseEventKind.Wheel"/> event.
    /// </summary>
    public int WheelDeltaX { get; init; }
}