// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>A cardinal direction / arm of a box-drawing cell. The value is its 2-bit field index in an
/// arm code (Up = bits 0–1, Right = 2–3, Down = 4–5, Left = 6–7).</summary>
internal enum Arm : byte
{
    Up = 0,
    Right = 1,
    Down = 2,
    Left = 3
}

/// <summary>
/// The topology-independent decoration a <see cref="Pen"/> contributes to a stroked cell — resolved
/// against the cell's final arm topology at flush (rounded only on a light corner, dash only on a
/// straight run, cap only on a lone arm).
/// </summary>
internal readonly record struct StrokeDecoration(CornerStyle Corners, LineDash Dash, EndCap Cap);
