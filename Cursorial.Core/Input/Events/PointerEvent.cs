namespace Cursorial.Core.Input;

/// <summary>
/// A non-mouse pointing-device event: pen, stylus, or finger touch. Reported only by devices
/// whose <see cref="PointerCapabilities"/> indicate support.
/// </summary>
/// <remarks>
/// No current Cursorial input source emits <see cref="PointerEvent"/>: no terminal protocol
/// in widespread use carries pen / stylus / touch input. The type is retained as
/// forward-compat surface for a future Windows console-host integration (and any analogous
/// POSIX path that might surface these), so consumer pattern-matches against
/// <see cref="InputEvent"/> can include a branch that becomes live without an API change.
/// </remarks>
public sealed record class PointerEvent : InputEvent
{
    /// <summary>Whether the pointer is a pen/stylus or a finger touch.</summary>
    public required PointerKind PointerKind { get; init; }

    /// <summary>Whether the pointer entered, moved, made/released contact, or left the device's range.</summary>
    public required PointerEventKind EventKind { get; init; }

    /// <summary>Position in cell rows/columns and (when available) pixels.</summary>
    public required CellPosition Position { get; init; }

    /// <summary>Stable identifier for the pointer instance, allowing multi-touch tracking across events.</summary>
    public required ulong PointerId { get; init; }

    /// <summary>Normalized pressure in [0, 1] when reported.</summary>
    public float? Pressure { get; init; }

    /// <summary>Tilt along the X axis in degrees [-90, 90] when reported.</summary>
    public float? TiltX { get; init; }

    /// <summary>Tilt along the Y axis in degrees [-90, 90] when reported.</summary>
    public float? TiltY { get; init; }

    /// <summary>Modifier keys held when the event occurred.</summary>
    public KeyModifiers Modifiers { get; init; }
}
