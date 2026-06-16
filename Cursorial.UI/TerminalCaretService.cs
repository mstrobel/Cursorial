using Cursorial.Output;

namespace Cursorial.UI;

/// <summary>
/// The frame-assembly caret registry (design doc §5.9): a focused editing control publishes an
/// <b>element-local</b> caret position and <see cref="CursorShape"/>; the service transforms it to
/// window coordinates during frame assembly and the render system writes the back-buffer cursor
/// state (<c>CellBuffer.CursorColumn/Row/Visible/Shape</c>) from it — the caret is the <em>real
/// terminal cursor</em>, so blinking is terminal-native (zero re-raster per phase) and the cursor
/// carries terminal-level semantics for assistive technology (the one v1 accessibility affordance).
/// Never re-rasters anything.
/// </summary>
public interface ITerminalCaretService
{
    /// <summary>
    /// Publishes (or updates) <paramref name="owner"/>'s caret at element-local
    /// (<paramref name="column"/>, <paramref name="row"/>) with the given <paramref name="shape"/>.
    /// The most recent publication wins frame assembly.
    /// </summary>
    void Publish(UIElement owner, int column, int row, CursorShape shape);

    /// <summary>Withdraws <paramref name="owner"/>'s publication (no-op when none exists).</summary>
    void Clear(UIElement owner);
}

/// <summary>
/// The assembled caret for one frame: the window-coordinate cursor position, its shape, and whether
/// it should be visible. <c>default</c> is the hidden caret. The render system copies this onto the
/// back buffer's cursor state after compositing (S6's <c>RenderFrame</c> leg).
/// </summary>
/// <param name="Visible">Whether the terminal cursor should be shown.</param>
/// <param name="Column">The window-coordinate column (meaningful while a publication exists, even when hidden).</param>
/// <param name="Row">The window-coordinate row.</param>
/// <param name="Shape">The DECSCUSR cursor shape.</param>
public readonly record struct TerminalCaretState(bool Visible, int Column, int Row, CursorShape Shape)
{
    /// <summary>The hidden caret (no publication, or the active publication is clipped out).</summary>
    public static TerminalCaretState Hidden => default;
}

/// <summary>
/// The S1-owned <see cref="ITerminalCaretService"/> implementation (design doc §5.9; punch 29's S1
/// leg): a publication registry plus the element→window transform leg. UI-thread only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transform timing.</b> <see cref="GetCaretState"/> runs the transform <b>live</b> via
/// <see cref="UIElement.TranslateToWindow"/> — call it during frame assembly, after the layout pass
/// and after <see cref="RenderTree.RunRenderPass"/>'s boundary walk, so arranged bounds, render
/// offsets, scroll offsets, and the refreshed boundary clips are all current. A caret outside its
/// zone's effective clip (scrolled out of view, hidden ancestor, covered viewport edge) assembles
/// as <see cref="TerminalCaretState.Hidden"/> — "clipped-out ⇒ CursorVisible = false".
/// </para>
/// <para>
/// <b>Stale-owner guarantee.</b> Publications from detached owners are dropped at assembly time
/// (and pruned from the registry), independent of the owner also calling <see cref="Clear"/> on
/// detach — the belt-and-braces contract: a removed-while-focused editor can never leave a stale
/// terminal cursor.
/// </para>
/// <para>
/// The consumer is the S6 frame loop's render system (it writes the <c>CellBuffer</c> cursor state after
/// compositing). With S4 windowing, the render system passes the <c>surfaceOffsetResolver</c> overload so
/// the winning publication's window-local caret is folded by its owning surface's screen offset — a caret
/// in a window/popup lands at the right absolute cell, not at the screen origin. "Only the focused window's
/// publication wins" is S4's leg of the contract — focus follows window activation, so the focused editor's
/// publication is the most recent one.
/// </para>
/// </remarks>
public sealed class TerminalCaretService : ITerminalCaretService
{
    private readonly struct Publication(UIElement owner, int column, int row, CursorShape shape)
    {
        public readonly UIElement Owner = owner;
        public readonly int Column = column;
        public readonly int Row = row;
        public readonly CursorShape Shape = shape;
    }

    private readonly List<Publication> _publications = [];

    /// <summary>
    /// The S6 wiring hook: invoked on every actual registry change (equality-gated — re-publishing
    /// the identical winning caret is a no-op). <see cref="UIApplication"/> points this at
    /// <c>RequestRender</c> so a pure caret move (arrow key, no content change) arms the Phase-6
    /// render gate — without it the terminal cursor would go stale until something else rendered.
    /// </summary>
    internal Action? StateChanged;

    /// <inheritdoc/>
    public void Publish(UIElement owner, int column, int row, CursorShape shape)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.VerifyAccess();

        // Equality gate: identical re-publication by the current winner changes nothing.
        if (_publications.Count > 0)
        {
            var winner = _publications[^1];
            if (ReferenceEquals(winner.Owner, owner)
                && winner.Column == column && winner.Row == row && winner.Shape == shape)
            {
                return;
            }
        }

        RemoveOwner(owner);
        _publications.Add(new Publication(owner, column, row, shape)); // most recent wins
        StateChanged?.Invoke();
    }

    /// <inheritdoc/>
    public void Clear(UIElement owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.VerifyAccess();
        if (RemoveOwner(owner))
            StateChanged?.Invoke();
    }

    /// <summary>The number of live publications (test observability; stale owners prune at assembly).</summary>
    public int PublicationCount => _publications.Count;

    /// <summary>
    /// Assembles the caret for this frame: drops detached owners, transforms the winning (most
    /// recent) publication to window coordinates via <see cref="UIElement.TranslateToWindow"/>
    /// (render and scroll offsets folded), gates visibility on effective visibility and the owner's
    /// refreshed zone clip, then folds the surface offset (S4's leg; 0 at P1). Allocation-free.
    /// </summary>
    public TerminalCaretState GetCaretState(int surfaceOffsetColumn = 0, int surfaceOffsetRow = 0)
        => GetCaretStateCore(surfaceOffsetColumn, surfaceOffsetRow, surfaceOffsetResolver: null);

    /// <summary>
    /// Assembles the caret, folding a <b>per-owner</b> surface offset (S4's leg): the winning publication's
    /// owner is mapped to its top-level surface's screen offset by <paramref name="surfaceOffsetResolver"/>,
    /// and that offset is added <em>after</em> the element→window-local transform — translating a caret
    /// published inside a window/popup surface from window-local cells to absolute screen cells. The resolver
    /// is invoked at most once per call (only when a live publication wins), so it must be allocation-free.
    /// </summary>
    public TerminalCaretState GetCaretState(Func<UIElement, (int Column, int Row)> surfaceOffsetResolver)
    {
        ArgumentNullException.ThrowIfNull(surfaceOffsetResolver);
        return GetCaretStateCore(0, 0, surfaceOffsetResolver);
    }

    private TerminalCaretState GetCaretStateCore(int baseOffsetColumn, int baseOffsetRow,
                                                 Func<UIElement, (int Column, int Row)>? surfaceOffsetResolver)
    {
        for (var i = _publications.Count - 1; i >= 0; i--)
        {
            var publication = _publications[i];
            var owner = publication.Owner;
            if (!owner.IsAttachedToTree)
            {
                _publications.RemoveAt(i); // stale-owner drop (doc §5.9)
                continue;
            }

            var (column, row) = owner.TranslateToWindow(publication.Column, publication.Row);
            var visible = owner.IsEffectivelyVisible && IsInsideZoneClip(owner, column, row);
            var (offsetColumn, offsetRow) = surfaceOffsetResolver is not null
                ? surfaceOffsetResolver(owner)        // the owning surface's screen offset (S4)
                : (baseOffsetColumn, baseOffsetRow);  // the constant-offset / origin path (P1 / tests)
            return new TerminalCaretState(visible, column + offsetColumn, row + offsetRow, publication.Shape);
        }

        return TerminalCaretState.Hidden;
    }

    private static bool IsInsideZoneClip(UIElement owner, int column, int row)
    {
        // The owning zone's effective clip already intersects every ancestor boundary clip (and is
        // Rect.Empty under hidden ancestors). Before the first render pass there is no zone yet —
        // effective visibility alone gates then.
        var zone = owner.ZoneRoot?.Zone;
        return zone is null || zone.EffectiveClip.Contains(column, row);
    }

    private bool RemoveOwner(UIElement owner)
    {
        for (var i = _publications.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_publications[i].Owner, owner))
            {
                _publications.RemoveAt(i);
                return true;
            }
        }

        return false;
    }
}
