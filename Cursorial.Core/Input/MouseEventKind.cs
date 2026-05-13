using Cursorial.Input.Events;

namespace Cursorial.Input;

/// <summary>
/// Distinguishes the kind of mouse activity in a <see cref="MouseEvent"/>.
/// </summary>
public enum MouseEventKind
{
    /// <summary>A button transitioned from up to down.</summary>
    ButtonDown,
    /// <summary>A button transitioned from down to up.</summary>
    ButtonUp,
    /// <summary>The pointer moved with no button held (any-event tracking).</summary>
    Move,
    /// <summary>The pointer moved with one or more buttons held.</summary>
    Drag,
    /// <summary>
    /// The wheel rotated; consult <see cref="MouseEvent.WheelDeltaY"/> and
    /// <see cref="MouseEvent.WheelDeltaX"/>.
    /// </summary>
    Wheel,
}
