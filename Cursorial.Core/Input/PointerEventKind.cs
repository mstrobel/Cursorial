namespace Cursorial.Core.Input;

/// <summary>
/// Distinguishes the lifecycle phase of a <see cref="PointerEvent"/>.
/// </summary>
public enum PointerEventKind
{
    /// <summary>The pointer became visible to the device — e.g. a pen hovered into range.</summary>
    Enter,
    /// <summary>The pointer made contact with the surface (pen touched, finger pressed).</summary>
    Down,
    /// <summary>The pointer moved.</summary>
    Move,
    /// <summary>The pointer broke contact with the surface.</summary>
    Up,
    /// <summary>The pointer left the device's range.</summary>
    Leave,
}
