using Cursorial.Input.Events;

namespace Cursorial.UI.Input;

/// <summary>
/// The dispatcher's window-topology consumption seam (design doc §7.6, matrix ND5): the
/// surface-level gating and scan S3 performs <b>before</b> delegating intra-surface descent to
/// S1's <c>RenderTree.HitTest</c>. S3 owns hover/enter-leave and all intra-surface routing; the
/// topology owns which surface a terminal position lands on and whether the window manager
/// consumed the event outright.
/// </summary>
/// <remarks>
/// At P2 there is exactly one implicit surface — the shown application root
/// (<see cref="SingleRootWindowTopology"/>): full-screen, never blocked, no light dismiss. S4's
/// <c>WindowManager</c> substitutes the real implementation at P7 (surface stack topmost-first
/// with signed composite-inclusive bounds, blocked-surface occlusion, light dismiss,
/// activation-on-press) with no dispatcher rewrite.
/// </remarks>
internal interface IWindowTopology
{
    /// <summary>
    /// Filters one mouse event at the surface level (doc §7.6). Returns <see langword="false"/>
    /// when the window manager swallowed the event (light dismiss, blocked-press attention,
    /// activation-on-press — never at P2); otherwise <see langword="true"/> with
    /// <paramref name="surfaceRoot"/> = the root element of the topmost surface under the
    /// position, or <see langword="null"/> when no surface is under it (no shown root, blocked
    /// occlusion) — the event then routes nowhere (ND5: dropped, never a throw).
    /// </summary>
    /// <param name="mouse">The device event being gated.</param>
    /// <param name="surfaceRoot">The hit surface's root element, or null for a miss.</param>
    /// <param name="surfaceColumn">The position translated into surface-local columns.</param>
    /// <param name="surfaceRow">The position translated into surface-local rows.</param>
    bool FilterMouseEvent(MouseEvent mouse, out UIElement? surfaceRoot, out int surfaceColumn, out int surfaceRow);
}

/// <summary>
/// The trivial P2 topology (matrix ND5): one implicit surface — the application's shown root —
/// at the terminal origin, so surface-local coordinates equal terminal coordinates. Never
/// swallows (no window-manager policy exists yet).
/// </summary>
internal sealed class SingleRootWindowTopology(UIApplication application) : IWindowTopology
{
    /// <inheritdoc/>
    public bool FilterMouseEvent(MouseEvent mouse, out UIElement? surfaceRoot, out int surfaceColumn, out int surfaceRow)
    {
        // The shown root is the surface only while it is attached with a live render tree —
        // mid-swap or never-shown states report "no surface" (N71: dropped, no throw).
        var root = application.RootElement;
        surfaceRoot = root is { IsAttachedToTree: true } && root.RenderTreeHost is not null ? root : null;
        surfaceColumn = mouse.Position.Column;
        surfaceRow = mouse.Position.Row;
        return true;
    }
}
