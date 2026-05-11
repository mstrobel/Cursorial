namespace Cursorial.Core.Input;

/// <summary>
/// A position within the terminal grid, reported in cell coordinates and optionally pixel
/// coordinates. Used by mouse, pen, touch, and other pointer-style events. Negative values may
/// be reported when the pointer is dragged outside the terminal viewport.
/// </summary>
/// <param name="Column">Zero-based column (cell).</param>
/// <param name="Row">Zero-based row (cell).</param>
/// <param name="PixelX">Pixel X coordinate when reported by the terminal; null otherwise.</param>
/// <param name="PixelY">Pixel Y coordinate when reported by the terminal; null otherwise.</param>
public readonly record struct CellPosition(int Column, int Row, int? PixelX = null, int? PixelY = null);
