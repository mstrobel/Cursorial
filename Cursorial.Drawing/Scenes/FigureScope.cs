// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// The disposable scope returned by <see cref="DrawingContext.BeginFigure()"/>. Dispose it (normally
/// via <c>using</c>) to close the figure:
/// <code>using (ctx.BeginFigure()) { /* strokes here form one figure */ }</code>
/// </summary>
/// <remarks>
/// Carries a figure-id token, not a stack position, so a double dispose (or a copied-then-disposed
/// value) is a safe no-op. <b>Figures do not nest</b> — despite what a nested <c>using</c> visually
/// implies, calling <see cref="DrawingContext.BeginFigure()"/> while a figure is already open throws.
/// </remarks>
public readonly struct FigureScope : IDisposable
{
    private readonly DrawingContext? _context;
    private readonly int _figureId;

    internal FigureScope(DrawingContext context, int figureId)
    {
        _context = context;
        _figureId = figureId;
    }

    /// <summary>Close this figure (no-op if it was already closed).</summary>
    public void Dispose() => _context?.EndFigure(_figureId);
}
