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

    /// <summary>
    /// A completed click — a press followed by a release on the same cell. Never reported by a
    /// terminal directly; synthesized by a click recognizer (see the mouse-click synthesizer) and
    /// always carries <see cref="InputEvent.Synthesized"/> = true. Its
    /// <see cref="MouseEvent.ClickCount"/> distinguishes single / double / triple clicks.
    /// </summary>
    Click
}