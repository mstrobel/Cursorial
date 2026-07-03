// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// The disposable scope returned by <see cref="DrawingContext.PushClip"/>,
/// <see cref="DrawingContext.PushTranslate"/>, and <see cref="DrawingContext.Push"/>. Dispose it
/// (normally via <c>using</c>) to pop the clip / translate it pushed. Unlike <see cref="FigureScope"/>,
/// these scopes <b>nest</b>: each push composes onto the active state — the clip intersects and the
/// translate adds — and disposing restores the parent state.
/// </summary>
/// <remarks>
/// The scope carries a stack-depth token; disposing pops back to (and including) that depth, so a leaked
/// or out-of-order scope is reconciled the same way the context closes a leaked figure when the draw
/// delegate returns. A double dispose (or a copied-then-disposed value) is a safe no-op.
/// </remarks>
public readonly struct DrawingStateScope : IDisposable
{
    private readonly DrawingContext? _context;
    private readonly int _depth;

    internal DrawingStateScope(DrawingContext context, int depth)
    {
        _context = context;
        _depth = depth;
    }

    /// <summary>Pop this scope, restoring the clip / translate in effect when it was pushed.</summary>
    public void Dispose() => _context?.PopTo(_depth);
}
