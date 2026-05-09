namespace GlowTerm.Core.Input;

/// <summary>
/// Identifies a single mouse button. Extended buttons beyond <see cref="X2"/> map to
/// <see cref="X3"/> and beyond when supported.
/// </summary>
public enum MouseButton
{
    None = 0,
    Left,
    Middle,
    Right,
    /// <summary>The "back" button on multi-button mice (typically navigates back in browsers).</summary>
    X1,
    /// <summary>The "forward" button on multi-button mice.</summary>
    X2,
    X3,
    X4,
    X5,
    X6,
    X7,
    X8,
}
