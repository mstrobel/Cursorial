namespace GlowTerm.Core.Input;

/// <summary>
/// Bitmask of mouse buttons currently held. Each <see cref="MouseButton"/> value (other than
/// <see cref="MouseButton.None"/>) corresponds to one bit here.
/// </summary>
[Flags]
public enum MouseButtonState : uint
{
    None = 0,
    Left = 1u << 0,
    Middle = 1u << 1,
    Right = 1u << 2,
    X1 = 1u << 3,
    X2 = 1u << 4,
    X3 = 1u << 5,
    X4 = 1u << 6,
    X5 = 1u << 7,
    X6 = 1u << 8,
    X7 = 1u << 9,
    X8 = 1u << 10,
}
