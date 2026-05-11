namespace GlowTerm.Core.Input;

/// <summary>
/// A mouse / pointer position, reported in cell coordinates and optionally pixel coordinates.
/// Negative values may be reported when the pointer is dragged outside the terminal viewport.
/// </summary>
/// <param name="Column">Zero-based column (cell).</param>
/// <param name="Row">Zero-based row (cell).</param>
/// <param name="PixelX">Pixel X coordinate when reported by the terminal; null otherwise.</param>
/// <param name="PixelY">Pixel Y coordinate when reported by the terminal; null otherwise.</param>
public readonly record struct MousePosition(int Column, int Row, int? PixelX = null, int? PixelY = null);
